using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MediaPlayerCore.YtDlp
{
    /// <summary>
    /// Metadata returned by yt-dlp --dump-json for a given URL.
    /// </summary>
    public class YtDlpMetadata
    {
        [JsonProperty("title")]
        public string? Title { get; set; }

        [JsonProperty("duration")]
        public double? Duration { get; set; }

        [JsonProperty("thumbnail")]
        public string? Thumbnail { get; set; }

        [JsonProperty("uploader")]
        public string? Uploader { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("webpage_url")]
        public string? WebpageUrl { get; set; }

        [JsonProperty("is_live")]
        public bool? IsLive { get; set; }

        [JsonProperty("http_headers")]
        public Dictionary<string, string>? HttpHeaders { get; set; }

        [JsonProperty("ext")]
        public string? Extension { get; set; }
    }

    /// <summary>
    /// Manages yt-dlp binary execution for resolving stream URLs and fetching metadata.
    /// Supports YouTube, Twitch, and 1000+ other sites.
    /// </summary>
    public class YtDlpManager : IDisposable
    {
        private string _ytDlpPath;
        private string? _cookiesPath;
        private int _preferredMaxHeight;
        private readonly object _lock = new object();
        private TcpListener? _cookieListener;
        private Thread? _cookieListenerThread;
        private bool _isListeningForCookies;

        public bool EnableSabrProxy { get; set; } = false;
        private HttpListener? _sabrProxyListener;
        private Thread? _sabrProxyThread;
        private bool _isProxyListening;
        private int _proxyPort = 0;

        private readonly ConcurrentDictionary<string, SabrSession> _sabrSessions = new();
        private readonly List<Process> _runningProcesses = new();
        private readonly SemaphoreSlim _bgutilServerGate = new(1, 1);
        private Process? _bgutilServerProcess;
        private volatile bool _bgutilServerReady;

        private sealed class SabrSession
        {
            public required string Id { get; init; }
            public required string Url { get; init; }
            public required string TempDir { get; init; }
            public required string OutputTemplate { get; init; }
            public string TempPath { get; set; } = "";
            public required bool IsLive { get; init; }
            public Process? Process { get; set; }
            public volatile bool Failed;
            public volatile bool DownloadFinished;
            public string? Error { get; set; }
            public int StreamConsumers;
        }

        public event EventHandler<string>? OnStatusUpdate;
        public event EventHandler<Exception>? OnError;

        /// <summary>
        /// Path to the yt-dlp executable.
        /// </summary>
        public string YtDlpPath
        {
            get => _ytDlpPath;
            set => _ytDlpPath = value;
        }

        /// <summary>
        /// Preferred max video height for quality selection (e.g. 360, 480, 720, 1080).
        /// 0 = best available.
        /// </summary>
        public int PreferredMaxHeight
        {
            get => _preferredMaxHeight;
            set => _preferredMaxHeight = value;
        }

        public YtDlpManager(string pluginDir, int preferredMaxHeight = 720)
        {
            _ytDlpPath = Path.Combine(pluginDir, "yt-dlp.exe");
            _preferredMaxHeight = preferredMaxHeight;
            _cookiesPath = FindCookiesFile();
            StartCookieListener();
        }

        private void StartCookieListener()
        {
            try
            {
                _cookieListener = new TcpListener(IPAddress.Loopback, 9696);
                _cookieListener.Start();

                _isListeningForCookies = true;
                _cookieListenerThread = new Thread(CookieListenerLoop)
                {
                    IsBackground = true,
                    Name = "VRCVideoCacherCookieListener"
                };
                _cookieListenerThread.Start();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new Exception("Failed to start VRCVideoCacher cookie listener. Port 9696 might be in use.", ex));
            }
        }

        private void CookieListenerLoop()
        {
            while (_isListeningForCookies && _cookieListener != null)
            {
                try
                {
                    using var client = _cookieListener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8);

                    string? line;
                    int contentLength = 0;
                    bool isPost = false;

                    // Read HTTP headers
                    while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        if (line.StartsWith("POST")) isPost = true;
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(line.Substring(15).Trim(), out int len))
                            {
                                contentLength = len;
                            }
                        }
                    }

                    if (isPost && contentLength > 0)
                    {
                        char[] bodyChars = new char[contentLength];
                        int read = reader.ReadBlock(bodyChars, 0, contentLength);
                        string body = new string(bodyChars, 0, read);

                        // Try to parse out the cookies and save them
                        if (!string.IsNullOrEmpty(body) && body.Contains(".youtube.com"))
                        {
                            SaveCookiesFromText(body, "VRCVideoCacher browser extension");
                            OnStatusUpdate?.Invoke(this, "Successfully processed cookies from extension!");
                        }
                    }

                    // Send a CORS-friendly 200 OK
                    string response = "HTTP/1.1 200 OK\r\n" +
                                      "Access-Control-Allow-Origin: *\r\n" +
                                      "Access-Control-Allow-Methods: POST, OPTIONS\r\n" +
                                      "Access-Control-Allow-Headers: Content-Type\r\n" +
                                      "Connection: close\r\n\r\nOK";
                    byte[] buffer = Encoding.UTF8.GetBytes(response);
                    stream.Write(buffer, 0, buffer.Length);
                }
                catch (SocketException)
                {
                    // Thrown when the listener is stopped/aborted
                    break;
                }
                catch (Exception e)
                {
                    OnError?.Invoke(this, new Exception("Error receiving cookies via TcpListener.", e));
                }
            }
        }

        public void Dispose()
        {
            _isListeningForCookies = false;
            _isProxyListening = false;
            try
            {
                _cookieListener?.Stop();
                _sabrProxyListener?.Stop();
            }
            catch { }

            lock (_runningProcesses)
            {
                foreach (var process in _runningProcesses)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                    catch { }
                }
                _runningProcesses.Clear();
            }

            foreach (var session in _sabrSessions.Values)
            {
                CleanupSabrSession(session);
            }
            _sabrSessions.Clear();

            try
            {
                if (_bgutilServerProcess != null && !_bgutilServerProcess.HasExited)
                {
                    _bgutilServerProcess.Kill(true);
                }
            }
            catch { }
            _bgutilServerProcess?.Dispose();
            _bgutilServerProcess = null;
            _bgutilServerGate.Dispose();
        }

        /// <summary>
        /// Returns true if a valid cookies file was found (e.g. from VRCVideoCacher).
        /// </summary>
        public bool HasCookies => !string.IsNullOrEmpty(_cookiesPath) && File.Exists(_cookiesPath);

        /// <summary>
        /// Returns true if the yt-dlp binary exists at the configured path.
        /// </summary>
        public bool IsAvailable()
        {
            return !string.IsNullOrEmpty(_ytDlpPath) && File.Exists(_ytDlpPath);
        }

        public static bool IsYouTubeSessionError(string? errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return false;
            return errorText.Contains("The page needs to be reloaded", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves a URL to a direct stream URL suitable for VLC playback.
        /// Returns null if resolution fails.
        /// </summary>
        public async Task<string[]?> ResolveStreamUrl(string url)
        {
            if (!IsAvailable())
            {
                OnError?.Invoke(this, new FileNotFoundException("yt-dlp binary not found at: " + _ytDlpPath));
                return null;
            }

            try
            {
                if (EnableSabrProxy && IsYouTubeUrl(url))
                {
                    if (_proxyPort == 0) StartProxyListener();
                    if (_proxyPort != 0)
                    {
                        await EnsureBgutilServerAsync();
                        bool isLive = IsYouTubeLiveUrl(url);
                        SabrSession session = StartSabrSession(url, isLive);
                        if (session.Failed)
                        {
                            OnStatusUpdate?.Invoke(this, "SABR Proxy failed to start. Falling back to direct yt-dlp resolution.");
                        }
                        else
                        {
                            string proxyUrl = $"http://127.0.0.1:{_proxyPort}/stream/{session.Id}";
                            OnStatusUpdate?.Invoke(this, "SABR Proxy active. Preparing stream via local proxy.");
                            return new string[] { proxyUrl };
                        }
                    }
                }

                OnStatusUpdate?.Invoke(this, "Resolving stream URL...");

                string formatArg = _preferredMaxHeight > 0
                  ? $"bv[height<={_preferredMaxHeight}]+ba/b"
                  : "bv+ba/b";

                string result = await RunYtDlp($"--get-url -f \"{formatArg}\" \"{url}\"");
                string[]? streamUrls = result?.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                if (streamUrls != null && streamUrls.Length > 0)
                {
                    OnStatusUpdate?.Invoke(this, $"Stream URL resolved ({streamUrls.Length} streams).");
                    return streamUrls;
                }

                OnError?.Invoke(this, new Exception("yt-dlp returned empty URL for: " + url));
                return null;
            } catch (Exception e)
            {
                OnError?.Invoke(this, e);
                return null;
            }
        }

        /// <summary>
        /// Fetches metadata (title, duration, uploader, thumbnail, etc.) for a URL.
        /// Returns null if fetching fails.
        /// </summary>
        public async Task<YtDlpMetadata?> GetMetadata(string url)
        {
            if (!IsAvailable())
            {
                return null;
            }

            try
            {
                string result = await RunYtDlp($"--dump-json --no-download --no-playlist \"{url}\"");
                if (!string.IsNullOrEmpty(result))
                {
                    var firstJsonLine = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                              .FirstOrDefault(l => l.TrimStart().StartsWith("{"));
                    if (firstJsonLine != null)
                    {
                        return JsonConvert.DeserializeObject<YtDlpMetadata>(firstJsonLine);
                    }
                }
            } catch (Exception e)
            {
                OnError?.Invoke(this, e);
            }
            return null;
        }

        /// <summary>
        /// Resolves a URL to multiple quality stream URLs.
        /// Returns an array where index 0 = audio-only, then ascending quality.
        /// Falls back to single URL if format listing fails.
        /// </summary>
        public async Task<string[]> ResolveMultiQualityUrls(string url)
        {
            if (!IsAvailable())
            {
                return Array.Empty<string>();
            }

            try
            {
                // Try to get URLs at specific quality levels
                var qualities = new[] { 360, 480, 720, 1080 };
                var urls = new List<string>();

                // Audio only
                string? audioUrl = await ResolveUrlWithFormat(url, "bestaudio");
                urls.Add(audioUrl ?? "");

                // Video at each quality level
                foreach (int height in qualities)
                {
                    string? qualityUrl = await ResolveUrlWithFormat(url, $"b[height<={height}]/b");
                    urls.Add(qualityUrl ?? "");
                }

                // If we got at least one valid URL, return the array
                if (urls.Any(u => !string.IsNullOrEmpty(u)))
                {
                    return urls.ToArray();
                }

                // Fallback: just get best URL
                string[]? bestUrls = await ResolveStreamUrl(url);
                if (bestUrls != null && bestUrls.Length > 0 && !string.IsNullOrEmpty(bestUrls[0]))
                {
                    return new string[] { bestUrls[0], bestUrls[0], bestUrls[0], bestUrls[0], bestUrls[0] };
                }
            } catch (Exception e)
            {
                OnError?.Invoke(this, e);
            }
            return Array.Empty<string>();
        }

        private static readonly System.Collections.Generic.HashSet<string> _knownFailedUrls = new();

        /// <summary>
        /// Marks a URL as known to fail with yt-dlp (e.g. 403 or Unsupported).
        /// </summary>
        public static void MarkUrlAsFailed(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            lock (_knownFailedUrls)
            {
                _knownFailedUrls.Add(url);
            }
        }

        /// <summary>
        /// Checks if the given URL is likely supported by yt-dlp
        /// (not a raw stream or local file).
        /// </summary>
        public static bool IsUrlSupported(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            
            lock (_knownFailedUrls)
            {
                if (_knownFailedUrls.Contains(url)) return false;
            }

            // Don't try yt-dlp on raw streams or local files
            if (url.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(url)) return false;
            // Must be an HTTP URL
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
              || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Attempts to self-update yt-dlp via yt-dlp -U.
        /// </summary>
        public async Task<bool> SelfUpdate()
        {
            if (!IsAvailable()) return false;

            try
            {
                OnStatusUpdate?.Invoke(this, "Updating yt-dlp...");
                string result = await RunYtDlp("-U", withCommonArgs: false);
                OnStatusUpdate?.Invoke(this, "yt-dlp update complete.");
                return true;
            } catch (Exception e)
            {
                OnError?.Invoke(this, e);
                return false;
            }
        }

        #region Private Helpers

        private const string Deno =
            "https://github.com/denoland/deno/releases/download/v2.8.2/deno-x86_64-pc-windows-msvc.zip";
        private const string YtDlpDownloadUrl =
          "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string ChromeCookieUnlockUrl =
          "https://github.com/seproDev/yt-dlp-ChromeCookieUnlock/releases/latest/download/yt-dlp-ChromeCookieUnlock.zip";

        private string PluginDir => Path.GetDirectoryName(_ytDlpPath) ?? ".";
        private string PluginsDir => Path.Combine(PluginDir, "yt-dlp-plugins");
        private string ChromeCookieUnlockPath => Path.Combine(PluginsDir, "yt-dlp-ChromeCookieUnlock.zip");
        private string DenoPath => Path.Combine(PluginDir, "deno.zip");

        /// <summary>
        /// Ensures yt-dlp and all dependencies are available.
        /// Downloads missing components, then self-updates yt-dlp.
        /// Call this at plugin startup on a background thread.
        /// </summary>
        public async Task EnsureAvailableAsync()
        {
            if (!IsAvailable())
            {
                await DownloadYtDlp();
            }

            // Download companion tools if missing
            await EnsureChromeCookieUnlock();
            await EnsureDeno();
            await EnsureFfmpeg();
            if (EnableSabrProxy)
            {
                await EnsurePotProvider();
                await EnsureBgutilServerAsync();
            }

            if (IsAvailable())
            {
                await SelfUpdate();
            }

            // Report cookie status
            if (HasCookies)
            {
                OnStatusUpdate?.Invoke(this, $"Using cookies from: {_cookiesPath}");
            } else if (!string.IsNullOrEmpty(CookieBrowser))
            {
                OnStatusUpdate?.Invoke(this, $"Using cookies from browser: {CookieBrowser}");
            } else
            {
                OnStatusUpdate?.Invoke(this, "No YouTube cookies found. Place cookies.txt in plugin dir, or install VRCVideoCacher browser extension.");
            }
        }

        /// <summary>
        /// Downloads yt-dlp.exe from GitHub releases to the plugin directory.
        /// </summary>
        private async Task DownloadYtDlp()
        {
            await DownloadFile(YtDlpDownloadUrl, _ytDlpPath, "yt-dlp");
        }

        /// <summary>
        /// Downloads the ChromeCookieUnlock plugin if missing.
        /// Placed in yt-dlp-plugins/ next to yt-dlp.exe so it's auto-discovered.
        /// </summary>
        private async Task EnsureChromeCookieUnlock()
        {
            if (File.Exists(ChromeCookieUnlockPath)) return;
            Directory.CreateDirectory(PluginsDir);
            await DownloadFile(ChromeCookieUnlockUrl, ChromeCookieUnlockPath, "ChromeCookieUnlock plugin");
        }

        /// <summary>
        /// Downloads the ChromeCookieUnlock plugin if missing.
        /// Placed in yt-dlp-plugins/ next to yt-dlp.exe so it's auto-discovered.
        /// </summary>
        private async Task EnsureDeno()
        {
            if (File.Exists(DenoExecutablePath)) return;
            await DownloadFile(Deno, DenoPath, "Deno");
            ZipFile.ExtractToDirectory(DenoPath, PluginDir);
        }

        /// <summary>
        /// Generic file downloader with temp-file-then-move pattern.
        /// </summary>
        private async Task DownloadFile(string url, string targetPath, string displayName)
        {
            try
            {
                string targetDir = Path.GetDirectoryName(targetPath) ?? ".";
                Directory.CreateDirectory(targetDir);

                OnStatusUpdate?.Invoke(this, $"Downloading {displayName}...");

                using var httpClient = new System.Net.Http.HttpClient();
                httpClient.Timeout = TimeSpan.FromMinutes(5);
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("XivMediaPlayer/1.0");

                using var response = await httpClient.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                // Write to a temp file first, then move (atomic-ish)
                string tempPath = targetPath + ".tmp";
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await response.Content.CopyToAsync(fs);
                }

                // Replace existing if present
                if (File.Exists(targetPath))
                {
                    File.Delete(targetPath);
                }
                File.Move(tempPath, targetPath);

                OnStatusUpdate?.Invoke(this, $"{displayName} downloaded.");
            } catch (Exception e)
            {
                OnError?.Invoke(this, new Exception($"Failed to download {displayName}: " + e.Message, e));
            }
        }

        private async Task<string?> ResolveUrlWithFormat(string url, string format)
        {
            try
            {
                string result = await RunYtDlp($"--get-url -f \"{format}\" \"{url}\"");
                return result?.Trim().Split('\n').FirstOrDefault()?.Trim();
            } catch
            {
                return null;
            }
        }

        private async Task<string> RunYtDlp(string arguments, bool withCommonArgs = true)
        {
            return await Task.Run(() =>
            {
                // Inject cookies if available (e.g. from VRCVideoCacher browser extension)
                string fullArgs = (withCommonArgs ? BuildCommonArgs() : "") + arguments;
                var psi = new ProcessStartInfo
                {
                    FileName = _ytDlpPath,
                    Arguments = fullArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    throw new Exception("Failed to start yt-dlp process");
                }

                lock (_runningProcesses)
                {
                    _runningProcesses.Add(process);
                }

                bool timedOut = false;
                using var timer = new Timer(_ =>
                {
                    timedOut = true;
                    try { process.Kill(true); } catch { }
                }, null, 30000, Timeout.Infinite);

                // Read stderr asynchronously to prevent buffer deadlocks
                string error = "";
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) error += e.Data + "\n"; };
                process.BeginErrorReadLine();

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                timer.Change(Timeout.Infinite, Timeout.Infinite);

                if (timedOut)
                {
                    throw new TimeoutException("yt-dlp timed out after 30 seconds");
                }

                if (process.ExitCode != 0 && string.IsNullOrEmpty(output))
                {
                    throw new Exception($"yt-dlp exited with code {process.ExitCode}: {error}");
                }

                lock (_runningProcesses)
                {
                    _runningProcesses.Remove(process);
                }

                return output;
            });
        }

        /// <summary>
        /// Looks for a cookies file in order of priority:
        /// 1. Plugin directory (cookies.txt — user-provided or auto-saved from clipboard)
        /// 2. VRCVideoCacher's youtube_cookies.txt (from browser extension)
        /// </summary>
        private string? FindCookiesFile()
        {
            // 1. Check plugin directory first
            string pluginDir = Path.GetDirectoryName(_ytDlpPath) ?? ".";
            string localCookies = Path.Combine(pluginDir, "cookies.txt");
            if (File.Exists(localCookies)) return localCookies;

            // 2. Check VRCVideoCacher's youtube_cookies.txt
            string vrcCookies = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCVideoCacher", "youtube_cookies.txt");
            if (File.Exists(vrcCookies)) return vrcCookies;

            return null;
        }

        public bool HasCookiesFile => FindCookiesFile() != null;

        /// <summary>
        /// Returns the path where cookies.txt will be saved (plugin directory).
        /// </summary>
        public string CookiesSavePath => Path.Combine(
          Path.GetDirectoryName(_ytDlpPath) ?? ".", "cookies.txt");

        /// <summary>
        /// Checks if text looks like Netscape cookie format (tab-separated, 7 fields per line).
        /// Used for auto-detecting cookies on the clipboard.
        /// </summary>
        public static bool IsNetscapeCookieFormat(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            var lines = text.Split('\n')
              .Select(l => l.Trim())
              .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
              .ToArray();

            if (lines.Length < 2) return false;

            // At least half the non-comment lines should be 7-field tab-separated
            int validCount = 0;
            foreach (var line in lines)
            {
                var parts = line.Split('\t');
                if (parts.Length == 7 && (parts[0].Contains('.') || parts[0] == "localhost"))
                {
                    validCount++;
                }
            }

            return validCount >= 2 && validCount >= lines.Length / 2;
        }

        /// <summary>
        /// Saves cookie text to the plugin directory and updates the cookie path.
        /// Returns true if saved successfully.
        /// </summary>
        public bool SaveCookiesFromText(string cookieText, string source = "clipboard")
        {
            try
            {
                File.WriteAllText(CookiesSavePath, cookieText);
                _cookiesPath = CookiesSavePath;
                OnStatusUpdate?.Invoke(this, $"YouTube cookies saved from {source}.");
                return true;
            } catch (Exception e)
            {
                OnError?.Invoke(this, new Exception("Failed to save cookies: " + e.Message, e));
                return false;
            }
        }

        /// <summary>
        /// Optional: set to a browser name (chrome, firefox, edge) to use
        /// yt-dlp's --cookies-from-browser feature instead of a cookie file.
        /// </summary>
        public string? CookieBrowser { get; set; }

        /// <summary>
        /// Builds the common argument prefix (cookies, etc.) for all yt-dlp calls.
        /// </summary>
        private string BuildCommonArgs()
        {
            string args = $"--extractor-args \"{BuildYouTubeExtractorArgs(isLive: false, includeSabrFormats: false)}\" --extractor-args \"youtubetab:skip=authcheck\" ";

            if (File.Exists(DenoExecutablePath))
            {
                args += $"--js-runtimes deno:{QuotedYtDlpPath(DenoExecutablePath)} ";
            }

            args += "--remote-components ejs:github --socket-timeout 30 ";

            // Cookie injection
            if (!string.IsNullOrEmpty(CookieBrowser))
            {
                args += $"--cookies-from-browser {CookieBrowser} ";
            } else if (HasCookies)
             {
                args += $"--cookies {QuotedYtDlpPath(_cookiesPath!)} ";
            }

            return args;
        }

        private static string NormalizePathForYtDlp(string path) => path.Replace('\\', '/');

        private static string QuotedYtDlpPath(string path) => $"\"{NormalizePathForYtDlp(path)}\"";

        private string BgutilServerDir => Path.Combine(PluginDir, "bgutil-pot-provider");
        private string BgutilServerWorkDir => Path.Combine(BgutilServerDir, "server");
        private string BgutilNodeModulesDir => Path.Combine(BgutilServerWorkDir, "node_modules");
        private string BgutilReadyMarker => Path.Combine(BgutilServerWorkDir, ".xivmp-deno-ready");

        private bool HasYouTubeAuth => HasCookies || !string.IsNullOrEmpty(CookieBrowser);

        private string BuildYouTubeExtractorArgs(bool isLive, bool includeSabrFormats)
        {
            // web/mweb need PO tokens from bgutil; tv works with cookies without tokens.
            string clients = HasYouTubeAuth
                ? (_bgutilServerReady ? "web,mweb,tv" : "tv")
                : (_bgutilServerReady ? "tv,mweb,web_embedded" : "tv,web_embedded");

            var parts = new List<string>
            {
                $"player_client={clients}",
                "player_js_version=actual",
                "player_js_variant=main",
            };

            if (includeSabrFormats)
            {
                // formats=duplicate is for listing only; it breaks single-file -o downloads.
                parts.Add(isLive ? "formats=sabr_live" : "");
            }

            return "youtube:" + string.Join(";", parts.Where(p => !string.IsNullOrEmpty(p)));
        }

        private string BuildSabrYouTubeExtractorArgs(bool isLive)
        {
            // web/mweb need PO tokens (bgutil server). tv is the no-token fallback.
            string clients = _bgutilServerReady ? "web,mweb,tv" : "tv";

            var parts = new List<string>
            {
                $"player_client={clients}",
                "player_js_version=actual",
                "player_js_variant=main",
            };

            if (isLive)
            {
                parts.Add("formats=sabr_live");
            }

            return "youtube:" + string.Join(";", parts);
        }

        private static bool IsClientDisconnect(Exception ex)
        {
            return ex is IOException or HttpListenerException;
        }

        private static bool IsYouTubeUrl(string url)
        {
            return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsYouTubeLiveUrl(string url)
        {
            return url.Contains("youtube.com/live/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("youtu.be/live/", StringComparison.OrdinalIgnoreCase)
                || (url.Contains("/live", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase));
        }

        private string DenoExecutablePath => Path.Combine(PluginDir, "deno.exe");

        private string BuildSabrFormatSelector()
        {
            string heightFilter = _preferredMaxHeight > 0 ? $"[height<={_preferredMaxHeight}]" : "";
            // Prefer standard DASH merge (works with PO tokens), then SABR merge, then single-file fallback.
            return $"bv{heightFilter}+ba/ba[protocol=sabr]+bv[protocol=sabr]{heightFilter}/b";
        }

        private string BuildSabrDownloadArgs(string url, bool isLive, string outputTemplate)
        {
            string args = "--extractor-args \"youtubetab:skip=authcheck\" ";
            args += $"--extractor-args \"{BuildSabrYouTubeExtractorArgs(isLive)}\" ";

            if (File.Exists(DenoExecutablePath))
            {
                args += $"--js-runtimes deno:{QuotedYtDlpPath(DenoExecutablePath)} ";
            }

            args += "--remote-components ejs:github --socket-timeout 30 --no-part --merge-output-format mkv ";

            if (!string.IsNullOrEmpty(CookieBrowser))
            {
                args += $"--cookies-from-browser {CookieBrowser} ";
            }
            else if (HasCookies)
            {
                args += $"--cookies {QuotedYtDlpPath(_cookiesPath!)} ";
            }

            string ffmpegPath = Path.Combine(PluginDir, "ffmpeg.exe");
            if (File.Exists(ffmpegPath))
            {
                args += $"--ffmpeg-location {QuotedYtDlpPath(ffmpegPath)} ";
            }

            if (isLive)
            {
                args += "--live-from-start ";
            }

            args += $"-f \"{BuildSabrFormatSelector()}\" -o {QuotedYtDlpPath(outputTemplate)} \"{url}\"";
            return args;
        }

        private static string? FindSabrOutputFile(SabrSession session)
        {
            if (!string.IsNullOrEmpty(session.TempPath) && File.Exists(session.TempPath))
            {
                return session.TempPath;
            }

            if (!Directory.Exists(session.TempDir)) return null;

            string expectedMerged = Path.Combine(session.TempDir, "stream.mkv");
            if (File.Exists(expectedMerged)) return expectedMerged;

            string? mkv = Directory.GetFiles(session.TempDir, "*.mkv")
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
            if (mkv != null) return mkv;

            return Directory.GetFiles(session.TempDir)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .FirstOrDefault();
        }

        private static long GetSabrOutputLength(SabrSession session)
        {
            string? path = FindSabrOutputFile(session);
            if (path == null) return 0;
            session.TempPath = path;
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private SabrSession StartSabrSession(string url, bool isLive)
        {
            string sessionId = Guid.NewGuid().ToString("N");
            string tempDir = Path.Combine(Path.GetTempPath(), $"xivmp-sabr-{sessionId}");
            Directory.CreateDirectory(tempDir);
            string outputTemplate = Path.Combine(tempDir, "stream.%(ext)s");

            var session = new SabrSession
            {
                Id = sessionId,
                Url = url,
                IsLive = isLive,
                TempDir = tempDir,
                OutputTemplate = outputTemplate,
                TempPath = Path.Combine(tempDir, "stream.mkv"),
            };

            string ffmpegPath = Path.Combine(PluginDir, "ffmpeg.exe");
            if (!File.Exists(ffmpegPath))
            {
                session.Failed = true;
                session.Error = "FFmpeg not found in plugin directory.";
                _sabrSessions[session.Id] = session;
                return session;
            }

            string fullArgs = BuildSabrDownloadArgs(url, isLive, session.OutputTemplate);
            var psi = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = fullArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var process = Process.Start(psi);
            if (process == null)
            {
                session.Failed = true;
                session.Error = "Failed to start yt-dlp.";
                _sabrSessions[session.Id] = session;
                return session;
            }

            session.Process = process;
            _sabrSessions[session.Id] = session;

            lock (_runningProcesses)
            {
                _runningProcesses.Add(process);
            }

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderr.AppendLine(e.Data);
                if (e.Data.Contains("ERROR:", StringComparison.OrdinalIgnoreCase)
                    || e.Data.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase))
                {
                    session.Failed = true;
                    session.Error = stderr.ToString();
                }
            };
            process.OutputDataReceived += (_, e) => { /* discard progress output */ };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            Task.Run(() =>
            {
                try
                {
                    process.WaitForExit();
                    session.DownloadFinished = true;
                    if (process.HasExited && process.ExitCode != 0)
                    {
                        session.Failed = true;
                        session.Error = stderr.Length > 0 ? stderr.ToString() : $"yt-dlp exited with code {process.ExitCode}";
                        OnError?.Invoke(this, new Exception($"SABR download failed: {session.Error}"));
                    }
                }
                finally
                {
                    lock (_runningProcesses)
                    {
                        _runningProcesses.Remove(process);
                    }

                    try { process.Dispose(); } catch { }
                    session.Process = null;
                }
            });

            return session;
        }

        private static void CleanupSabrSession(SabrSession session)
        {
            try
            {
                var process = session.Process;
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(true);
                    }
                    catch { }
                    try { process.Dispose(); } catch { }
                    session.Process = null;
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(session.TempDir))
                {
                    Directory.Delete(session.TempDir, true);
                }
            }
            catch { }
        }

        private static bool IsDownloadStillRunning(SabrSession session)
        {
            if (session.DownloadFinished) return false;
            try
            {
                return session.Process != null && !session.Process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private bool WaitForSabrData(SabrSession session, int timeoutMs = 90000)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (session.Failed) return false;

                if (GetSabrOutputLength(session) >= 65536) return true;

                if (session.Process?.HasExited == true)
                {
                    if (session.Failed) return false;
                    return GetSabrOutputLength(session) > 0;
                }

                if (session.DownloadFinished)
                {
                    if (session.Failed) return false;
                    return GetSabrOutputLength(session) > 0;
                }

                Thread.Sleep(100);
            }

            return GetSabrOutputLength(session) > 0;
        }

        private void StreamSabrFileToResponse(SabrSession session, HttpListenerContext context)
        {
            string? outputPath = FindSabrOutputFile(session);
            if (outputPath == null)
            {
                return;
            }

            session.TempPath = outputPath;

            context.Response.ContentType = "video/x-matroska";
            context.Response.SendChunked = true;
            context.Response.AddHeader("Cache-Control", "no-cache");

            using var fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[128 * 1024];
            long offset = 0;

            while (true)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                int read = fs.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    try
                    {
                        context.Response.OutputStream.Write(buffer, 0, read);
                        context.Response.OutputStream.Flush();
                    }
                    catch (Exception ex) when (IsClientDisconnect(ex))
                    {
                        break;
                    }

                    offset += read;
                    continue;
                }

                if (!IsDownloadStillRunning(session))
                {
                    fs.Seek(offset, SeekOrigin.Begin);
                    read = fs.Read(buffer, 0, buffer.Length);
                    if (read > 0)
                    {
                        try
                        {
                            context.Response.OutputStream.Write(buffer, 0, read);
                            context.Response.OutputStream.Flush();
                        }
                        catch (Exception ex) when (IsClientDisconnect(ex)) { }
                        offset += read;
                        continue;
                    }
                    break;
                }

                if (session.Failed) break;
                Thread.Sleep(50);
            }
        }



        private void StartProxyListener()
        {
            try
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                _proxyPort = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();

                _sabrProxyListener = new HttpListener();
                _sabrProxyListener.Prefixes.Add($"http://127.0.0.1:{_proxyPort}/");
                _sabrProxyListener.Start();
                _isProxyListening = true;

                _sabrProxyThread = new Thread(ProxyListenerLoop)
                {
                    IsBackground = true,
                    Name = "XivMediaPlayerSABRProxy"
                };
                _sabrProxyThread.Start();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new Exception("Failed to start SABR proxy listener.", ex));
            }
        }

        private void ProxyListenerLoop()
        {
            while (_isProxyListening && _sabrProxyListener != null)
            {
                try
                {
                    var context = _sabrProxyListener.GetContext();
                    Task.Run(() => HandleProxyRequest(context));
                }
                catch (HttpListenerException) { break; }
                catch (Exception e) { OnError?.Invoke(this, new Exception("SABR proxy error.", e)); }
            }
        }

        private void HandleProxyRequest(HttpListenerContext context)
        {
            SabrSession? session = null;
            try
            {
                string path = context.Request.Url?.AbsolutePath ?? "";
                if (path.StartsWith("/stream/", StringComparison.OrdinalIgnoreCase))
                {
                    string sessionId = path["/stream/".Length..];
                    if (!_sabrSessions.TryGetValue(sessionId, out session) || session == null)
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                        return;
                    }
                }
                else if (path.Equals("/play", StringComparison.OrdinalIgnoreCase))
                {
                    string url = context.Request.QueryString["url"] ?? "";
                    if (string.IsNullOrEmpty(url))
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Close();
                        return;
                    }

                    bool isLive = context.Request.QueryString["live"] == "1" || IsYouTubeLiveUrl(url);
                    session = StartSabrSession(url, isLive);
                }
                else
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    return;
                }

                if (session.Failed)
                {
                    context.Response.StatusCode = 503;
                    context.Response.StatusDescription = "SABR download failed";
                    context.Response.Close();
                    if (!string.IsNullOrEmpty(session.Error))
                    {
                        OnError?.Invoke(this, new Exception($"SABR proxy could not start download: {session.Error}"));
                    }
                    return;
                }

                OnStatusUpdate?.Invoke(this, "SABR Proxy streaming to player...");
                Interlocked.Increment(ref session.StreamConsumers);
                if (!WaitForSabrData(session))
                {
                    context.Response.StatusCode = 502;
                    context.Response.StatusDescription = "SABR stream unavailable";
                    context.Response.Close();
                    string detail = session.Error ?? "Timed out waiting for SABR stream data.";
                    OnError?.Invoke(this, new Exception($"SABR proxy stream unavailable: {detail}"));
                    Interlocked.Decrement(ref session.StreamConsumers);
                    return;
                }

                StreamSabrFileToResponse(session, context);
            }
            catch (Exception ex) when (!IsClientDisconnect(ex))
            {
                OnError?.Invoke(this, new Exception("SABR proxy request failed.", ex));
            }
            finally
            {
                try { context.Response.Close(); } catch { }

                if (session != null && Interlocked.Decrement(ref session.StreamConsumers) == 0)
                {
                    _sabrSessions.TryRemove(session.Id, out _);
                    CleanupSabrSession(session);
                }
            }
        }

        private const string FfmpegZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        private const string PotProviderZipUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/releases/latest/download/bgutil-ytdlp-pot-provider.zip";

        private async Task EnsureFfmpeg()
        {
            string ffmpegPath = Path.Combine(PluginDir, "ffmpeg.exe");
            if (File.Exists(ffmpegPath)) return;

            string zipPath = Path.Combine(PluginDir, "ffmpeg.zip");
            await DownloadFile(FfmpegZipUrl, zipPath, "FFmpeg");

            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    entry.ExtractToFile(ffmpegPath, true);
                    OnStatusUpdate?.Invoke(this, "FFmpeg extracted successfully.");
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new Exception("Failed to extract FFmpeg", ex));
            }
            finally
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
            }
        }

        private async Task EnsurePotProvider()
        {
            Directory.CreateDirectory(PluginsDir);
            string marker = Path.Combine(PluginsDir, ".bgutil-plugin-installed");
            if (File.Exists(marker)) return;

            string potProviderZipPath = Path.Combine(PluginsDir, "bgutil-ytdlp-pot-provider.zip");
            if (!File.Exists(potProviderZipPath))
            {
                await DownloadFile(PotProviderZipUrl, potProviderZipPath, "PO Token provider plugin");
            }

            try
            {
                ZipFile.ExtractToDirectory(potProviderZipPath, PluginsDir, overwriteFiles: true);
                File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
                OnStatusUpdate?.Invoke(this, "PO Token provider plugin installed.");
            }
            catch (Exception ex)
            {
                OnError?.Invoke(this, new Exception("Failed to extract PO Token provider plugin.", ex));
            }
        }

        private const string BgutilReleaseZipUrl = "https://github.com/Brainicism/bgutil-ytdlp-pot-provider/archive/refs/tags/1.3.1.zip";

        private async Task EnsureBgutilServerAsync()
        {
            if (!EnableSabrProxy) return;
            if (_bgutilServerReady) return;

            await _bgutilServerGate.WaitAsync();
            try
            {
                if (_bgutilServerReady) return;
                if (await IsBgutilServerRespondingAsync())
                {
                    _bgutilServerReady = true;
                    return;
                }

                if (!File.Exists(DenoExecutablePath))
                {
                    OnError?.Invoke(this, new Exception("Deno is required for the PO Token provider but was not found."));
                    return;
                }

                await DownloadBgutilServerIfNeededAsync();
                if (!await SetupBgutilServerDepsAsync())
                {
                    OnStatusUpdate?.Invoke(this, "PO Token provider unavailable; using tv client without PO tokens.");
                    return;
                }

                if (await IsBgutilServerRespondingAsync())
                {
                    _bgutilServerReady = true;
                    OnStatusUpdate?.Invoke(this, "PO Token provider server already running on port 4416.");
                    return;
                }

                StartBgutilServerProcess();
                if (await WaitForBgutilServerReadyAsync())
                {
                    _bgutilServerReady = true;
                    OnStatusUpdate?.Invoke(this, "PO Token provider server ready on port 4416.");
                }
                else
                {
                    OnError?.Invoke(this, new Exception("PO Token provider server failed to start on http://127.0.0.1:4416."));
                }
            }
            finally
            {
                _bgutilServerGate.Release();
            }
        }

        private async Task DownloadBgutilServerIfNeededAsync()
        {
            if (Directory.Exists(Path.Combine(BgutilServerWorkDir, "src"))) return;

            OnStatusUpdate?.Invoke(this, "Downloading PO Token provider server...");
            string zipPath = Path.Combine(PluginDir, "bgutil-pot-server.zip");
            await DownloadFile(BgutilReleaseZipUrl, zipPath, "PO Token provider server");

            string extractDir = Path.Combine(PluginDir, "bgutil-pot-server-extract");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            string? sourceDir = Directory.GetDirectories(extractDir).FirstOrDefault();
            if (sourceDir == null)
            {
                throw new Exception("PO Token provider archive was empty.");
            }

            if (Directory.Exists(BgutilServerDir)) Directory.Delete(BgutilServerDir, true);
            Directory.Move(sourceDir, BgutilServerDir);
            Directory.Delete(extractDir, true);
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }

        private async Task<bool> SetupBgutilServerDepsAsync()
        {
            if (Directory.Exists(BgutilNodeModulesDir))
            {
                if (!File.Exists(BgutilReadyMarker))
                {
                    File.WriteAllText(BgutilReadyMarker, DateTime.UtcNow.ToString("O"));
                }
                return true;
            }

            if (!Directory.Exists(BgutilServerWorkDir))
            {
                OnError?.Invoke(this, new Exception($"PO Token provider server directory not found: {BgutilServerWorkDir}"));
                return false;
            }

            OnStatusUpdate?.Invoke(this, "Setting up PO Token provider (first run may take 1-2 minutes)...");
            var (ok, output) = await RunProcessAsync(
                DenoExecutablePath,
                "install --allow-scripts --frozen",
                BgutilServerWorkDir,
                TimeSpan.FromMinutes(10));

            if (!ok)
            {
                OnError?.Invoke(this, new Exception(
                    "PO Token provider setup failed while running deno install.\n" +
                    (string.IsNullOrWhiteSpace(output) ? "No output captured." : output)));
                return false;
            }

            if (!Directory.Exists(BgutilNodeModulesDir))
            {
                OnError?.Invoke(this, new Exception(
                    "deno install completed but node_modules was not created.\n" + output));
                return false;
            }

            File.WriteAllText(BgutilReadyMarker, DateTime.UtcNow.ToString("O"));
            return true;
        }

        private void StartBgutilServerProcess()
        {
            if (_bgutilServerProcess != null)
            {
                try
                {
                    if (!_bgutilServerProcess.HasExited) return;
                }
                catch { }
                _bgutilServerProcess.Dispose();
                _bgutilServerProcess = null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = DenoExecutablePath,
                Arguments = "run --allow-env --allow-net --allow-ffi=. --allow-read=. ../src/main.ts",
                WorkingDirectory = BgutilNodeModulesDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _bgutilServerProcess = Process.Start(psi);
            if (_bgutilServerProcess == null)
            {
                throw new Exception("Failed to start PO Token provider server process.");
            }

            _bgutilServerProcess.OutputDataReceived += (_, e) => { /* discard */ };
            _bgutilServerProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                if (e.Data.Contains("EADDRINUSE", StringComparison.OrdinalIgnoreCase)
                    || e.Data.Contains("falling back", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                OnError?.Invoke(this, new Exception($"[bgutil] {e.Data}"));
            };
            _bgutilServerProcess.BeginOutputReadLine();
            _bgutilServerProcess.BeginErrorReadLine();
        }

        private static async Task<bool> IsBgutilServerRespondingAsync()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var response = await client.GetAsync("http://127.0.0.1:4416/ping");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> WaitForBgutilServerReadyAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsBgutilServerRespondingAsync()) return true;
                await Task.Delay(500);
            }
            return false;
        }

        private static async Task<(bool Success, string Output)> RunProcessAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout)
        {
            return await Task.Run(() =>
            {
                var output = new StringBuilder();
                var psi = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var process = Process.Start(psi);
                if (process == null) return (false, "Failed to start process.");

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) output.AppendLine(e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(true); } catch { }
                    output.AppendLine($"Process timed out after {timeout.TotalMinutes:0} minutes.");
                    return (false, output.ToString());
                }

                return (process.ExitCode == 0, output.ToString());
            });
        }

        #endregion
    }
}
