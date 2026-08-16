using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MediaPlayerCore
{
    public class StreamProxy : IDisposable
    {
        private static readonly Lazy<StreamProxy> _instance = new Lazy<StreamProxy>(() => new StreamProxy());
        public static StreamProxy Instance => _instance.Value;

        private HttpListener _listener;
        private TcpListener? _wineListener;
        private int _port;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<string, ProxySession> _sessions = new ConcurrentDictionary<string, ProxySession>();
        private static readonly bool IsWineRuntime = DetectWineRuntime();

        /// <summary>
        /// Wine currently aborts the process when HttpListener cancels an HTTP
        /// request (httpapi.dll.HttpCancelHttpRequest is unimplemented). The
        /// direct VLC path remains usable, but the Windows HTTP API proxy must
        /// never be started there.
        /// </summary>
        public static bool IsAvailable => !IsWineRuntime;
        public static string TransportName => IsWineRuntime ? "TcpListener (Wine-compatible)" : "HttpListener";

        public class ProxySession
        {
            public string OriginalM3u8Url { get; set; }
            public string PreFetchedM3u8Content { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public HttpClient Client { get; set; }
            public TemporaryMediaCache? Cache { get; set; }
        }

        /// <summary>
        /// Caches data locally to prevent reseeking and reseeking from and unseekable source.
        /// </summary>
        public sealed class TemporaryMediaCache : IDisposable
        {
            private readonly object _sync = new();
            private readonly CancellationTokenSource _cancellation = new();
            private TaskCompletionSource<bool> _changed = NewSignal();
            private long _availableBytes;
            private long? _totalBytes;
            private string? _contentType;
            private bool _completed;
            private Exception? _failure;

            public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                $"xivmp-media-{Guid.NewGuid():N}.cache");

            public void Start(HttpClient client, string url)
            {
                _ = Task.Run(() => DownloadAsync(client, url, _cancellation.Token));
            }

            public async Task WaitForMetadataAsync()
            {
                while (true)
                {
                    Task wait;
                    lock (_sync)
                    {
                        if (_totalBytes.HasValue || _contentType != null || _completed || _failure != null) return;
                        wait = _changed.Task;
                    }
                    await wait.ConfigureAwait(false);
                }
            }

            public (long AvailableBytes, long? TotalBytes, string? ContentType, bool Completed, Exception? Failure) Snapshot()
            {
                lock (_sync) return (_availableBytes, _totalBytes, _contentType, _completed, _failure);
            }

            public async Task WaitForBytesAsync(long requiredBytes)
            {
                while (true)
                {
                    Task wait;
                    lock (_sync)
                    {
                        if (_availableBytes >= requiredBytes || _completed || _failure != null) return;
                        wait = _changed.Task;
                    }
                    await wait.ConfigureAwait(false);
                }
            }

            public async Task CopyToAsync(Stream destination, long offset, long? endOffset)
            {
                byte[] buffer = new byte[81920];
                long position = offset;
                using var file = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                file.Position = offset;

                while (!endOffset.HasValue || position <= endOffset.Value)
                {
                    await WaitForBytesAsync(position + 1).ConfigureAwait(false);
                    var state = Snapshot();
                    if (state.AvailableBytes <= position)
                    {
                        // Download finished or failed before this range. The
                        // response ends naturally; the caller has already sent
                        // the correct bounded response when a total was known.
                        break;
                    }

                    int bytesToRead = (int)Math.Min(buffer.Length, state.AvailableBytes - position);
                    if (endOffset.HasValue)
                    {
                        bytesToRead = (int)Math.Min(bytesToRead, endOffset.Value - position + 1);
                    }

                    int read = await file.ReadAsync(buffer, 0, bytesToRead).ConfigureAwait(false);
                    if (read == 0)
                    {
                        // A writer update can become visible before this handle
                        // observes the new length. Wait for the next update.
                        await Task.Delay(10).ConfigureAwait(false);
                        continue;
                    }

                    await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    position += read;
                }
            }

            private async Task DownloadAsync(HttpClient client, string url, CancellationToken cancellationToken)
            {
                try
                {
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    await using var destination = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    lock (_sync)
                    {
                        _totalBytes = response.Content.Headers.ContentLength;
                        _contentType = response.Content.Headers.ContentType?.ToString();
                        SignalChanged();
                    }

                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                        lock (_sync)
                        {
                            _availableBytes += read;
                            SignalChanged();
                        }
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && _cancellation.IsCancellationRequested))
                {
                    lock (_sync)
                    {
                        _failure = ex;
                        SignalChanged();
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        _completed = true;
                        SignalChanged();
                    }
                }
            }

            private void SignalChanged()
            {
                var previous = _changed;
                _changed = NewSignal();
                previous.TrySetResult(true);
            }

            private static TaskCompletionSource<bool> NewSignal()
                => new(TaskCreationOptions.RunContinuationsAsynchronously);

            public void Dispose()
            {
                _cancellation.Cancel();
                _cancellation.Dispose();
                try { File.Delete(Path); } catch { }
            }
        }

        private StreamProxy()
        {
            _port = 40000 + new Random().Next(1000);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            if (IsWineRuntime)
            {
                StartWineListener();
                return;
            }
            if (_listener.IsListening) return;
            try
            {
                _listener.Start();
                Task.Run(() => AcceptLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamProxy] Failed to start listener: {ex.Message}");
            }
        }

        public string RegisterStream(string m3u8Url, Dictionary<string, string> headers, string preFetchedM3u8Content = null)
        {
            // The Wine socket transport currently covers direct media, which
            // is where the HTTP range/cache proxy is needed. Leave HLS direct
            // until its playlist rewriter is moved to the same transport.
            if (IsWineRuntime) return m3u8Url;
            Start();
            ClearSessions();
            string sessionId = Guid.NewGuid().ToString("N");

            var client = CreateStreamingHttpClient(headers, m3u8Url);

            if (string.IsNullOrEmpty(preFetchedM3u8Content))
            {
                preFetchedM3u8Content = PrefetchHlsPlaylist(client, m3u8Url);
            }

            _sessions[sessionId] = new ProxySession
            {
                OriginalM3u8Url = m3u8Url,
                PreFetchedM3u8Content = preFetchedM3u8Content,
                Headers = headers,
                Client = client
            };

            return $"http://127.0.0.1:{_port}/stream.m3u8?sid={sessionId}";
        }

        private HttpClient CreateStreamingHttpClient(Dictionary<string, string>? headers, string targetUrl)
        {
            var handler = new HttpClientHandler();
            handler.UseCookies = false;
            if (handler.SupportsAutomaticDecompression)
            {
                handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            }

            var client = new HttpClient(handler);
            bool hasUserAgent = false;
            bool hasAccept = false;
            if (headers != null)
            {
                foreach (var kvp in headers)
                {
                    if (kvp.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) hasUserAgent = true;
                    if (kvp.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) hasAccept = true;
                    try { client.DefaultRequestHeaders.TryAddWithoutValidation(kvp.Key, kvp.Value); } catch { }
                }
            }
            if (!hasUserAgent)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            }
            if (!hasAccept)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            }

            bool isYouTubeCdn = targetUrl.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase)
                || targetUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);
            if (isYouTubeCdn)
            {
                if (!client.DefaultRequestHeaders.TryGetValues("Referer", out _))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                }

                if (!client.DefaultRequestHeaders.TryGetValues("Origin", out _))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                }
            }

            bool isTwitchCdn = targetUrl.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)
                || targetUrl.Contains("ttvnw.net", StringComparison.OrdinalIgnoreCase);
            if (isTwitchCdn)
            {
                if (!client.DefaultRequestHeaders.TryGetValues("Referer", out _))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://www.twitch.tv/");
                }

                if (!client.DefaultRequestHeaders.TryGetValues("Origin", out _))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://www.twitch.tv");
                }
            }

            return client;
        }

        private static string PrefetchHlsPlaylist(HttpClient client, string m3u8Url)
        {
            using var response = client.GetAsync(m3u8Url).ConfigureAwait(false).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"HLS playlist fetch failed ({(int)response.StatusCode} {response.ReasonPhrase}). YouTube live requires valid cookies.");
            }

            string text = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("HLS playlist fetch returned invalid data (not M3U8).");
            }

            return text;
        }

        public string RegisterDirectMediaSession(string mediaUrl, Dictionary<string, string>? headers = null)
        {
            if (string.IsNullOrEmpty(mediaUrl)) return string.Empty;
            Start();
            ClearSessions();
            string sessionId = Guid.NewGuid().ToString("N");
            
            var client = CreateStreamingHttpClient(headers, mediaUrl);
            var cache = new TemporaryMediaCache();
            cache.Start(client, mediaUrl);

            _sessions[sessionId] = new ProxySession { OriginalM3u8Url = mediaUrl, Headers = headers, Client = client, Cache = cache };

            string targetBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(mediaUrl));
            return $"http://127.0.0.1:{_port}/proxy_media?sid={sessionId}&target={Uri.EscapeDataString(targetBase64)}";
        }

        /// <summary>
        /// Attempts to recover the original upstream URL from a proxy session ID.
        /// Returns null if the session is not found or has expired.
        /// </summary>
        public string GetOriginalUrl(string sessionId)
        {
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var session))
            {
                return session.OriginalM3u8Url;
            }
            return null;
        }

        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequest(context));
                }
                catch { }
            }
        }

        private void StartWineListener()
        {
            if (_wineListener != null) return;
            _wineListener = new TcpListener(IPAddress.Loopback, 0);
            _wineListener.Start();
            _port = ((IPEndPoint)_wineListener.LocalEndpoint).Port;
            Task.Run(() => WineAcceptLoop(_cts.Token));
        }

        private async Task WineAcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _wineListener != null)
            {
                try
                {
                    var client = await _wineListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleWineRequestAsync(client));
                }
                catch when (token.IsCancellationRequested) { }
                catch { }
            }
        }

        private async Task HandleWineRequestAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true))
            {
                try
                {
                    string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(requestLine)) return;
                    string[] requestParts = requestLine.Split(' ');
                    string target = requestParts.Length > 1 ? requestParts[1] : "/";
                    string? range = null;
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().ConfigureAwait(false)))
                    {
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line[6..].Trim();
                    }

                    var uri = new Uri("http://127.0.0.1" + target);
                    string sid = uri.Query.TrimStart('?').Split('&')
                        .Select(p => p.Split('=', 2))
                        .FirstOrDefault(p => p.Length == 2 && p[0] == "sid")?[1] ?? "";
                    if (uri.AbsolutePath != "/proxy_media" || !_sessions.TryGetValue(sid, out var session) || session.Cache == null)
                    {
                        await WriteWineHeadersAsync(stream, 404, "Not Found", null, 0).ConfigureAwait(false);
                        return;
                    }

                    await ServeWineCachedMediaAsync(stream, range, session.Cache).ConfigureAwait(false);
                }
                catch { }
            }
        }

        private async Task HandleRequest(HttpListenerContext context)
        {
            try
            {
                var req = context.Request;
                var res = context.Response;

                string path = req.Url.LocalPath;
                string sid = req.QueryString["sid"];

                if (string.IsNullOrEmpty(sid) || !_sessions.TryGetValue(sid, out var session))
                {
                    res.StatusCode = 404;
                    res.Close();
                    return;
                }

                if (path == "/stream.m3u8")
                {
                    // Fetch original m3u8
                    string m3u8Url = req.QueryString["target"] != null 
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]))
                        : session.OriginalM3u8Url;

                    string text;
                    try 
                    {
                        var response = await session.Client.GetAsync(m3u8Url);
                        if (!response.IsSuccessStatusCode)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[StreamProxy] HLS playlist fetch failed ({(int)response.StatusCode}) for {m3u8Url[..Math.Min(m3u8Url.Length, 120)]}...");
                            res.StatusCode = (int)response.StatusCode;
                            res.StatusDescription = response.ReasonPhrase;
                            res.Close();
                            return;
                        }

                        text = await response.Content.ReadAsStringAsync();
                        if (!text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                        {
                            System.Diagnostics.Debug.WriteLine($"[StreamProxy] HLS playlist fetch returned invalid data for {m3u8Url[..Math.Min(m3u8Url.Length, 120)]}...");
                            res.StatusCode = 502;
                            res.Close();
                            return;
                        }
                    } 
                    catch (Exception netEx)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[StreamProxy] HLS fetch failed for {m3u8Url}: {netEx.Message}");
                        res.StatusCode = 502;
                        res.Close();
                        return;
                    }

                    // Rewrite URLs
                    Uri baseUri = new Uri(m3u8Url);
                    var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var sb = new StringBuilder();

                    foreach (var line in lines)
                    {
                        sb.AppendLine(RewriteHlsLine(baseUri, line, sid));
                    }

                    byte[] outBytes = Encoding.UTF8.GetBytes(sb.ToString());
                    res.ContentType = "application/vnd.apple.mpegurl";
                    res.ContentLength64 = outBytes.Length;
                    await res.OutputStream.WriteAsync(outBytes, 0, outBytes.Length);
                }
                else if (path == "/stream.ts")
                {
                    string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]));
                    using var response = await session.Client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                    {
                        res.StatusCode = (int)response.StatusCode;
                        res.StatusDescription = response.ReasonPhrase;
                        System.Diagnostics.Debug.WriteLine(
                            $"[StreamProxy] HLS segment request failed ({(int)response.StatusCode}) for {targetUrl[..Math.Min(targetUrl.Length, 120)]}...");
                        res.Close();
                        return;
                    }

                    res.ContentType = response.Content.Headers.ContentType?.ToString() ?? "video/MP2T";
                    if (response.Content.Headers.ContentLength.HasValue)
                        res.ContentLength64 = response.Content.Headers.ContentLength.Value;

                    await response.Content.CopyToAsync(res.OutputStream);
                }
                else if (path == "/proxy_media")
                {
                    if (session.Cache != null)
                    {
                        await ServeCachedMediaAsync(req, res, session.Cache);
                        return;
                    }

                    string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]));
                    var requestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl);

                    long requestedOffset = 0;
                    long? requestedEnd = null;
                    string rangeHeader = req.Headers["Range"];
                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                    {
                        string[] rangeParts = rangeHeader.Substring("bytes=".Length).Split('-', 2);
                        if (long.TryParse(rangeParts[0], out long offset))
                        {
                            requestedOffset = offset;
                            if (rangeParts.Length == 2 && long.TryParse(rangeParts[1], out long end))
                            {
                                requestedEnd = end;
                            }
                            requestMessage.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, requestedEnd);
                        }
                    }

                    using var response = await session.Client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead);

                    if (!response.IsSuccessStatusCode)
                    {
                        res.StatusCode = (int)response.StatusCode;
                        res.StatusDescription = response.ReasonPhrase;
                        System.Diagnostics.Debug.WriteLine(
                            $"[StreamProxy] Upstream media request failed ({(int)response.StatusCode}) for {targetUrl[..Math.Min(targetUrl.Length, 120)]}...");
                        res.Close();
                        return;
                    }

                    res.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

                    using var stream = await response.Content.ReadAsStreamAsync();

                    if (response.StatusCode == HttpStatusCode.OK
                        && (requestedOffset > 0 || requestedEnd.HasValue))
                    {
                        // SERVER IGNORED RANGE REQUEST. WE MUST MANUALLY DISCARD BYTES.
                        long bytesToDiscard = requestedOffset;
                        byte[] discardBuffer = new byte[81920]; // 80KB buffer
                        while (bytesToDiscard > 0)
                        {
                            int toRead = (int)Math.Min(bytesToDiscard, discardBuffer.Length);
                            int read = await stream.ReadAsync(discardBuffer, 0, toRead);
                            if (read == 0) break; // EOF
                            bytesToDiscard -= read;
                        }

                        long totalLength = response.Content.Headers.ContentLength ?? 0;
                        if (totalLength <= 0 || requestedOffset >= totalLength)
                        {
                            res.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                            if (totalLength > 0) res.Headers["Content-Range"] = $"bytes */{totalLength}";
                            return;
                        }

                        long lastByte = Math.Min(requestedEnd ?? totalLength - 1, totalLength - 1);
                        if (lastByte < requestedOffset)
                        {
                            res.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                            res.Headers["Content-Range"] = $"bytes */{totalLength}";
                            return;
                        }

                        res.StatusCode = (int)HttpStatusCode.PartialContent;
                        res.Headers["Accept-Ranges"] = "bytes";
                        res.ContentLength64 = lastByte - requestedOffset + 1;
                        res.Headers["Content-Range"] = $"bytes {requestedOffset}-{lastByte}/{totalLength}";
                        await CopyBytesAsync(stream, res.OutputStream, res.ContentLength64);
                    }
                    else
                    {
                        res.StatusCode = (int)response.StatusCode;
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            res.ContentLength64 = response.Content.Headers.ContentLength.Value;
                            // The proxy can satisfy future ranges by reopening
                            // and discarding from this static download.
                            res.Headers["Accept-Ranges"] = "bytes";
                        }
                        else
                        {
                            res.SendChunked = true;
                        }

                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            res.Headers["Content-Range"] = response.Content.Headers.ContentRange?.ToString();
                            res.Headers["Accept-Ranges"] = "bytes";
                        }

                        await stream.CopyToAsync(res.OutputStream);
                    }
                }
                else
                {
                    res.StatusCode = 404;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StreamProxy] Error handling request: {ex.Message}");
                try { context.Response.StatusCode = 500; } catch { }
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static async Task CopyBytesAsync(Stream source, Stream destination, long bytesToCopy)
        {
            byte[] buffer = new byte[81920];
            while (bytesToCopy > 0)
            {
                int read = await source.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, bytesToCopy));
                if (read == 0) break;
                await destination.WriteAsync(buffer, 0, read);
                bytesToCopy -= read;
            }
        }

        private static async Task ServeCachedMediaAsync(HttpListenerRequest request, HttpListenerResponse response,
            TemporaryMediaCache cache)
        {
            await cache.WaitForMetadataAsync().ConfigureAwait(false);
            var state = cache.Snapshot();
            if (state.Failure != null)
            {
                response.StatusCode = (int)HttpStatusCode.BadGateway;
                return;
            }

            long offset = 0;
            long? requestedEnd = null;
            string? rangeHeader = request.Headers["Range"];
            bool isRangeRequest = !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);
            if (isRangeRequest)
            {
                string[] parts = rangeHeader!.Substring("bytes=".Length).Split('-', 2);
                if (!long.TryParse(parts[0], out offset) || offset < 0)
                {
                    response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                    return;
                }
                if (parts.Length == 2 && long.TryParse(parts[1], out long parsedEnd)) requestedEnd = parsedEnd;
            }

            if (state.TotalBytes.HasValue && offset >= state.TotalBytes.Value)
            {
                response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
                response.Headers["Content-Range"] = $"bytes */{state.TotalBytes.Value}";
                return;
            }

            response.ContentType = state.ContentType ?? "application/octet-stream";
            response.Headers["Accept-Ranges"] = "bytes";
            long? end = requestedEnd;
            if (state.TotalBytes.HasValue)
            {
                end = Math.Min(end ?? state.TotalBytes.Value - 1, state.TotalBytes.Value - 1);
            }

            if (isRangeRequest)
            {
                response.StatusCode = (int)HttpStatusCode.PartialContent;
                if (state.TotalBytes.HasValue)
                {
                    response.Headers["Content-Range"] = $"bytes {offset}-{end}/{state.TotalBytes.Value}";
                    response.ContentLength64 = end!.Value - offset + 1;
                }
                else
                {
                    response.SendChunked = true;
                }
            }
            else if (state.TotalBytes.HasValue)
            {
                response.ContentLength64 = state.TotalBytes.Value;
            }
            else
            {
                response.SendChunked = true;
            }

            await cache.CopyToAsync(response.OutputStream, offset, end).ConfigureAwait(false);
        }

        private static async Task ServeWineCachedMediaAsync(Stream stream, string? rangeHeader, TemporaryMediaCache cache)
        {
            await cache.WaitForMetadataAsync().ConfigureAwait(false);
            var state = cache.Snapshot();
            if (state.Failure != null) { await WriteWineHeadersAsync(stream, 502, "Bad Gateway", null, 0); return; }

            long offset = 0;
            long? requestedEnd = null;
            bool isRange = !string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);
            if (isRange)
            {
                var parts = rangeHeader!.Substring(6).Split('-', 2);
                if (!long.TryParse(parts[0], out offset) || offset < 0) { await WriteWineHeadersAsync(stream, 416, "Range Not Satisfiable", null, 0); return; }
                if (parts.Length == 2 && long.TryParse(parts[1], out var parsedEnd)) requestedEnd = parsedEnd;
            }
            if (!state.TotalBytes.HasValue || offset >= state.TotalBytes.Value) { await WriteWineHeadersAsync(stream, 416, "Range Not Satisfiable", null, 0); return; }

            long end = Math.Min(requestedEnd ?? state.TotalBytes.Value - 1, state.TotalBytes.Value - 1);
            long length = end - offset + 1;
            var headers = new Dictionary<string, string> {
                ["Content-Type"] = state.ContentType ?? "application/octet-stream",
                ["Accept-Ranges"] = "bytes"
            };
            if (isRange) headers["Content-Range"] = $"bytes {offset}-{end}/{state.TotalBytes.Value}";
            await WriteWineHeadersAsync(stream, isRange ? 206 : 200, isRange ? "Partial Content" : "OK", headers,
                isRange ? length : state.TotalBytes.Value).ConfigureAwait(false);
            await cache.CopyToAsync(stream, offset, isRange ? end : null).ConfigureAwait(false);
        }

        private static Task WriteWineHeadersAsync(Stream stream, int status, string reason,
            Dictionary<string, string>? headers, long contentLength)
        {
            var text = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nContent-Length: {contentLength}\r\nConnection: close\r\n");
            if (headers != null) foreach (var header in headers) text.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            text.Append("\r\n");
            byte[] bytes = Encoding.ASCII.GetBytes(text.ToString());
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        private string RewriteProxiedUrl(Uri baseUri, string originalUrl, string sid)
        {
            if (!Uri.TryCreate(baseUri, originalUrl, out Uri absoluteUrl))
            {
                return originalUrl;
            }

            string absolute = absoluteUrl.ToString();
            string targetBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(absolute));

            if (absolute.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return $"http://127.0.0.1:{_port}/stream.m3u8?sid={sid}&target={Uri.EscapeDataString(targetBase64)}";
            }

            return $"http://127.0.0.1:{_port}/proxy_media?sid={sid}&target={Uri.EscapeDataString(targetBase64)}";
        }

        private string RewriteHlsLine(Uri baseUri, string line, string sid)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return line;
            }

            if (!line.StartsWith("#"))
            {
                return RewriteProxiedUrl(baseUri, line.Trim(), sid);
            }

            int uriIndex = line.IndexOf("URI=\"", StringComparison.OrdinalIgnoreCase);
            if (uriIndex < 0)
            {
                return line;
            }

            int valueStart = uriIndex + 5;
            int valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd < 0)
            {
                return line;
            }

            string uriValue = line.Substring(valueStart, valueEnd - valueStart);
            string rewritten = RewriteProxiedUrl(baseUri, uriValue, sid);
            return line.Substring(0, valueStart) + rewritten + line.Substring(valueEnd);
        }

        /// <summary>
        /// Releases all proxy sessions and their HttpClients.
        /// </summary>
        public void ClearSessions()
        {
            foreach (string sessionId in _sessions.Keys.ToArray())
            {
                RemoveSession(sessionId);
            }
        }

        private void RemoveSession(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                try { session.Cache?.Dispose(); } catch { }
                try { session.Client?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            try { _wineListener?.Stop(); } catch { }
            ClearSessions();
        }

        private static bool DetectWineRuntime()
        {
            // Environment markers cover Proton/XIVLauncher setups where the
            // Wine loader does not expose wine_get_version through the normal
            // NativeLibrary lookup.
            string[] wineMarkers = { "WINEPREFIX", "WINELOADER", "WINEDLLPATH", "STEAM_COMPAT_DATA_PATH", "PROTON_PREFIX" };
            if (wineMarkers.Any(marker => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(marker))))
            {
                return true;
            }

            IntPtr module = IntPtr.Zero;
            try
            {
                if (!NativeLibrary.TryLoad("ntdll.dll", out module)) return false;
                return NativeLibrary.TryGetExport(module, "wine_get_version", out _);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (module != IntPtr.Zero) NativeLibrary.Free(module);
            }
        }
    }
}
