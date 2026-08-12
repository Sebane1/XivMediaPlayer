using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        private int _port;
        private CancellationTokenSource _cts;
        private ConcurrentDictionary<string, ProxySession> _sessions = new ConcurrentDictionary<string, ProxySession>();

        public class ProxySession
        {
            public string OriginalM3u8Url { get; set; }
            public string PreFetchedM3u8Content { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public HttpClient Client { get; set; }
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

            _sessions[sessionId] = new ProxySession { OriginalM3u8Url = mediaUrl, Headers = headers, Client = client };

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
                    string targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(req.QueryString["target"]));
                    var requestMessage = new HttpRequestMessage(HttpMethod.Get, targetUrl);

                    long requestedOffset = 0;
                    string rangeHeader = req.Headers["Range"];
                    if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
                    {
                        string rangeVal = rangeHeader.Substring("bytes=".Length).Split('-')[0];
                        if (long.TryParse(rangeVal, out long offset))
                        {
                            requestedOffset = offset;
                            requestMessage.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
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

                    if (response.StatusCode == HttpStatusCode.OK && requestedOffset > 0)
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

                        res.StatusCode = 206; // Trick VLC into thinking the server honored the Range request
                        long totalLength = response.Content.Headers.ContentLength ?? 0;
                        if (totalLength > 0)
                        {
                            res.ContentLength64 = totalLength - requestedOffset;
                            res.Headers["Content-Range"] = $"bytes {requestedOffset}-{totalLength - 1}/{totalLength}";
                        }
                        else
                        {
                            res.SendChunked = true;
                            res.Headers["Content-Range"] = $"bytes {requestedOffset}-/*";
                        }
                    }
                    else
                    {
                        res.StatusCode = (int)response.StatusCode;
                        if (response.Content.Headers.ContentLength.HasValue)
                        {
                            res.ContentLength64 = response.Content.Headers.ContentLength.Value;
                        }
                        else
                        {
                            res.SendChunked = true;
                        }

                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            res.Headers["Content-Range"] = response.Content.Headers.ContentRange?.ToString();
                        }
                    }

                    await stream.CopyToAsync(res.OutputStream);
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
                try { session.Client?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            ClearSessions();
        }
    }
}
