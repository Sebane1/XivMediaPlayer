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

        [JsonProperty("manifest_url")]
        public string? ManifestUrl { get; set; }

        [JsonProperty("webpage_url")]
        public string? WebpageUrl { get; set; }

        /// <summary>Best URL for playback (manifest or progressive).</summary>
        public string? PlaybackUrl => !string.IsNullOrEmpty(Url) ? Url : ManifestUrl;

        [JsonProperty("is_live")]
        public bool? IsLive { get; set; }

        [JsonProperty("live_status")]
        public string? LiveStatus { get; set; }

        [JsonProperty("http_headers")]
        public Dictionary<string, string>? HttpHeaders { get; set; }

        [JsonProperty("ext")]
        public string? Extension { get; set; }

        /// <summary>Approximate merged file size in bytes (from filesize_approx).</summary>
        public long? FilesizeApprox { get; set; }

        /// <summary>True for active or upcoming broadcasts; false for VOD and finished live replays.</summary>
        public bool IsLiveBroadcast =>
            IsLive == true
            || string.Equals(LiveStatus, "is_live", StringComparison.OrdinalIgnoreCase)
            || string.Equals(LiveStatus, "is_upcoming", StringComparison.OrdinalIgnoreCase);
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

        public bool EnableSabrProxy { get; set; } = true;
        private HttpListener? _sabrProxyListener;
        private Thread? _sabrProxyThread;
        private bool _isProxyListening;
        private int _proxyPort = 0;

        private readonly ConcurrentDictionary<string, SabrSession> _sabrSessions = new();
        private readonly ConcurrentDictionary<string, long> _expectedFilesizeByUrl = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<string> _pendingSabrDirDeletes = new();
        private readonly ConcurrentDictionary<string, (bool? IsLive, DateTime ExpiresUtc)> _youTubeLiveProbeCache = new();
        private readonly List<Process> _runningProcesses = new();
        private readonly SemaphoreSlim _bgutilServerGate = new(1, 1);
        private Process? _bgutilServerProcess;
        private volatile bool _bgutilServerReady;
        private readonly StringBuilder _bgutilServerLog = new();
        private int _youTubeSetupRunning;

        /// <summary>HTTP headers from the most recent yt-dlp format resolve (-j).</summary>
        public Dictionary<string, string>? LastResolvedHttpHeaders { get; private set; }

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
            public volatile float DownloadPercent = -1f;
            public long EstimatedFinalBytes;
            public long LastReportedBytes = -1;
            public float LastReportedPercent = -1f;
            public long LastStatusReportTick;
            public long LastDirectoryScanTick;
            public long CachedBufferedBytes;
            public string? Error { get; set; }
            public int StreamConsumers;
            public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        }

        public static bool IsSabrProxyUrl(string? url)
        {
            return !string.IsNullOrEmpty(url)
                && url.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                && url.Contains("/stream/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSabrLocalFile(string? path)
        {
            return !string.IsNullOrEmpty(path)
                && path.Contains("xivmp-sabr-", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSabrMediaPath(string? path)
            => IsSabrProxyUrl(path) || IsSabrLocalFile(path);

        public readonly struct SabrBufferStatus
        {
            public long BufferedBytes { get; init; }
            public bool IsDownloading { get; init; }
            public float DownloadPercent { get; init; }
        }

        public bool TryGetSabrBufferStatus(string? mediaPath, out SabrBufferStatus status)
        {
            status = default;
            if (string.IsNullOrEmpty(mediaPath) || !IsSabrLocalFile(mediaPath))
            {
                return false;
            }

            foreach (SabrSession session in _sabrSessions.Values)
            {
                if (!SessionMatchesMediaPath(session, mediaPath))
                {
                    continue;
                }

                status = new SabrBufferStatus
                {
                    BufferedBytes = GetCachedSabrBufferedBytes(session),
                    IsDownloading = IsDownloadStillRunning(session),
                    DownloadPercent = session.DownloadPercent,
                };
                return true;
            }

            return false;
        }

        private static long GetCachedSabrBufferedBytes(SabrSession session)
        {
            if (session.CachedBufferedBytes > 0)
            {
                return session.CachedBufferedBytes;
            }

            if (session.LastReportedBytes > 0)
            {
                return session.LastReportedBytes;
            }

            return GetSabrOutputLength(session);
        }

        private static bool SessionMatchesMediaPath(SabrSession session, string mediaPath)
        {
            if (mediaPath.StartsWith(session.TempDir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? output = FindSabrOutputFile(session);
            return output != null
                && string.Equals(output, mediaPath, StringComparison.OrdinalIgnoreCase);
        }

        private long GetSabrBufferedBytes(SabrSession session, bool allowDirectoryScan = false)
        {
            long muxBytes = GetSabrOutputLength(session);
            long best = muxBytes;
            long now = Environment.TickCount64;

            if (allowDirectoryScan
                && muxBytes < 262144
                && now - session.LastDirectoryScanTick > 2000)
            {
                session.LastDirectoryScanTick = now;
                long dirBytes = GetDirectorySizeBytes(session.TempDir);
                best = Math.Max(best, dirBytes);
            }

            if (session.DownloadPercent >= 0f && session.EstimatedFinalBytes > 0)
            {
                long estimated = (long)(session.EstimatedFinalBytes * session.DownloadPercent);
                best = Math.Max(best, estimated);
            }

            if (best > 0)
            {
                session.CachedBufferedBytes = best;
            }

            return best;
        }

        private void ReportSabrBufferProgress(SabrSession session, bool force = false)
        {
            long now = Environment.TickCount64;
            bool timeElapsed = now - session.LastStatusReportTick > 2000;
            long bytes = GetSabrBufferedBytes(session, allowDirectoryScan: force || timeElapsed);
            float pct = session.DownloadPercent;

            const long minByteDelta = 512 * 1024;
            bool bytesChanged = session.LastReportedBytes < 0
                || bytes - session.LastReportedBytes >= minByteDelta;
            bool pctChanged = pct >= 0f && Math.Abs(pct - session.LastReportedPercent) > 0.05f;

            if (!force && !bytesChanged && !pctChanged && !timeElapsed)
            {
                return;
            }

            session.LastReportedBytes = bytes;
            session.LastReportedPercent = pct;
            session.LastStatusReportTick = now;
            session.CachedBufferedBytes = bytes;
            OnStatusUpdate?.Invoke(this, FormatSabrBufferStatus(session, bytes));
        }

        private static string FormatSabrBufferStatus(SabrSession session, long bytes)
        {
            double mb = bytes / (1024.0 * 1024.0);
            if (session.DownloadPercent >= 0f)
            {
                return $"SABR buffering... ({mb:0.#} MB ready, {session.DownloadPercent * 100f:0}%)";
            }

            return $"SABR buffering... ({mb:0.#} MB ready)";
        }

        /// <summary>
        /// Registers expected merged file size from yt-dlp metadata (filesize_approx).
        /// </summary>
        public void RegisterExpectedFilesize(string url, long bytes)
        {
            if (string.IsNullOrWhiteSpace(url) || bytes <= 0)
            {
                return;
            }

            _expectedFilesizeByUrl[url] = bytes;
            foreach (SabrSession session in _sabrSessions.Values)
            {
                if (string.Equals(session.Url, url, StringComparison.OrdinalIgnoreCase)
                    && session.EstimatedFinalBytes <= 0)
                {
                    session.EstimatedFinalBytes = bytes;
                }
            }
        }

        private SabrSession? FindSabrSessionForPath(string mediaPath)
        {
            foreach (SabrSession session in _sabrSessions.Values)
            {
                if (SessionMatchesMediaPath(session, mediaPath))
                {
                    return session;
                }
            }

            return null;
        }

        /// <summary>
        /// True when the local SABR file is complete enough to seek anywhere (not just when yt-dlp exits).
        /// </summary>
        public bool IsSabrFileFullyBuffered(string? mediaPath, long fullDurationMs, long muxedDurationMs)
        {
            if (string.IsNullOrEmpty(mediaPath) || !IsSabrLocalFile(mediaPath))
            {
                return true;
            }

            SabrSession? session = FindSabrSessionForPath(mediaPath);
            if (session != null && IsDownloadStillRunning(session))
            {
                return false;
            }

            long fileBytes = TryGetFileLength(mediaPath);
            const long durationMarginMs = 5000;
            const double bytesCompleteRatio = 0.99;

            if (session != null)
            {
                if (session.Failed && fileBytes < 262144)
                {
                    return false;
                }

                long expectedBytes = session.EstimatedFinalBytes;
                if (expectedBytes > 0 && fileBytes < (long)(expectedBytes * bytesCompleteRatio))
                {
                    return false;
                }

                if (session.DownloadPercent >= 0f && session.DownloadPercent < 0.98f)
                {
                    return false;
                }
            }

            if (fullDurationMs > durationMarginMs && muxedDurationMs > 0)
            {
                return muxedDurationMs >= fullDurationMs - durationMarginMs;
            }

            // Mux probe unavailable on a finished download — fall back to byte/percent signals.
            if (session != null && session.DownloadFinished && !session.Failed)
            {
                if (session.DownloadPercent >= 0.99f)
                {
                    return true;
                }

                long expectedBytes = session.EstimatedFinalBytes;
                if (expectedBytes > 0 && fileBytes >= (long)(expectedBytes * bytesCompleteRatio))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsSabrDownloadActiveForPath(string? mediaPath)
        {
            if (string.IsNullOrEmpty(mediaPath) || !IsSabrLocalFile(mediaPath))
            {
                return false;
            }

            SabrSession? session = FindSabrSessionForPath(mediaPath);
            return session != null && IsDownloadStillRunning(session);
        }

        /// <summary>
        /// Stops and removes all active SABR download sessions.
        /// </summary>
        public void ReleaseSabrSessions()
        {
            foreach (var session in _sabrSessions.Values.ToArray())
            {
                if (_sabrSessions.TryRemove(session.Id, out _))
                {
                    CleanupSabrSession(session);
                }
            }

            ProcessPendingSabrDirDeletes();
            CleanupOrphanedSabrTempDirs();
        }

        /// <summary>
        /// Deletes leftover SABR temp folders from prior sessions (e.g. after a crash or failed cleanup).
        /// Returns approximate bytes freed.
        /// </summary>
        public long CleanupOrphanedSabrTempDirs()
        {
            long freedBytes = 0;
            try
            {
                var activeDirs = new HashSet<string>(
                    _sabrSessions.Values.Select(s => s.TempDir),
                    StringComparer.OrdinalIgnoreCase);

                foreach (string dir in Directory.EnumerateDirectories(Path.GetTempPath(), "xivmp-sabr-*"))
                {
                    if (activeDirs.Contains(dir))
                    {
                        continue;
                    }

                    long size = GetDirectorySizeBytes(dir);
                    if (TryDeleteSabrTempDir(dir))
                    {
                        freedBytes += size;
                    }
                    else
                    {
                        QueueSabrTempDirDelete(dir);
                    }
                }
            }
            catch { }

            ProcessPendingSabrDirDeletes();
            return freedBytes;
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
            Task.Run(CleanupOrphanedSabrTempDirs);
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
            CleanupOrphanedSabrTempDirs();

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

        private void StopBgutilServerProcess()
        {
            try
            {
                if (_bgutilServerProcess != null && !_bgutilServerProcess.HasExited)
                {
                    _bgutilServerProcess.Kill(true);
                    _bgutilServerProcess.WaitForExit(3000);
                }
            }
            catch { }
            _bgutilServerProcess?.Dispose();
            _bgutilServerProcess = null;
        }

        private void KillPluginDenoProcesses()
        {
            StopBgutilServerProcess();

            string denoExe = DenoExecutablePath;
            if (!File.Exists(denoExe))
            {
                return;
            }

            foreach (Process process in Process.GetProcessesByName("deno"))
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    string? processPath = process.MainModule?.FileName;
                    if (processPath != null
                        && string.Equals(processPath, denoExe, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static async Task<bool> TryDeleteDirectoryRobustAsync(string path)
        {
            if (!Directory.Exists(path))
            {
                return true;
            }

            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(path, true);
                    if (!Directory.Exists(path))
                    {
                        return true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }

                await Task.Delay(400 * (attempt + 1)).ConfigureAwait(false);
            }

            if (OperatingSystem.IsWindows() && TryRobocopyMirrorDelete(path))
            {
                return true;
            }

            try
            {
                string trashPath = path + ".old." + DateTime.UtcNow.Ticks;
                Directory.Move(path, trashPath);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    await TryDeleteDirectoryRobustAsync(trashPath).ConfigureAwait(false);
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryRobocopyMirrorDelete(string path)
        {
            string emptyDir = Path.Combine(Path.GetTempPath(), "xivmp-empty-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(emptyDir);
                var psi = new ProcessStartInfo
                {
                    FileName = "robocopy",
                    Arguments = $"\"{emptyDir}\" \"{path}\" /MIR /NFL /NDL /NJH /NJS /NC /NS /NP",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using Process? robocopy = Process.Start(psi);
                robocopy?.WaitForExit(15000);
                Directory.Delete(emptyDir, true);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }

                return !Directory.Exists(path);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(emptyDir))
                    {
                        Directory.Delete(emptyDir, true);
                    }
                }
                catch
                {
                }

                return false;
            }
        }

        private async Task CleanupStaleBgutilInstallDirsAsync()
        {
            if (!Directory.Exists(BgutilServerWorkDir))
            {
                return;
            }

            foreach (string staleDir in Directory.GetDirectories(BgutilServerWorkDir, "node_modules.old.*"))
            {
                await TryDeleteDirectoryRobustAsync(staleDir).ConfigureAwait(false);
            }
        }

        private static Exception CreateBgutilResetFailedException(Exception inner)
        {
            return new Exception(
                "Could not reset YouTube helper files (often 'css-calc' or another file in node_modules is locked). " +
                "Fully close FFXIV, open Task Manager, end any 'deno' tasks, then click Fix YouTube setup again. " +
                "If it still fails, delete the folder: bgutil-pot-provider/server/node_modules",
                inner);
        }

        private async Task<bool> TryClearBgutilInstallArtifactsAsync()
        {
            if (File.Exists(BgutilReadyMarker))
            {
                try { File.Delete(BgutilReadyMarker); } catch { }
            }

            string legacyMarker = Path.Combine(BgutilServerWorkDir, ".xivmp-deno-ready");
            if (File.Exists(legacyMarker))
            {
                try { File.Delete(legacyMarker); } catch { }
            }

            if (!Directory.Exists(BgutilNodeModulesDir))
            {
                return true;
            }

            KillPluginDenoProcesses();
            await Task.Delay(500).ConfigureAwait(false);

            if (await TryDeleteDirectoryRobustAsync(BgutilNodeModulesDir).ConfigureAwait(false))
            {
                return true;
            }

            OnError?.Invoke(this, CreateBgutilResetFailedException(
                new IOException($"Access denied while deleting {BgutilNodeModulesDir}")));
            return false;
        }

        private async Task<bool> ResetYouTubeHelperAsync()
        {
            await _bgutilServerGate.WaitAsync().ConfigureAwait(false);
            try
            {
                _bgutilServerReady = false;
                return await TryClearBgutilInstallArtifactsAsync().ConfigureAwait(false);
            }
            finally
            {
                _bgutilServerGate.Release();
            }
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

        /// <summary>True when the local PO Token helper server is running (YouTube SABR).</summary>
        public bool IsPoTokenServerReady => _bgutilServerReady;

        /// <summary>True while Fix YouTube setup / first-run helper install is running.</summary>
        public bool IsYouTubeSetupRunning => Interlocked.CompareExchange(ref _youTubeSetupRunning, 0, 0) != 0;

        /// <summary>
        /// Clears a failed YouTube helper install and runs setup again.
        /// Use when Deno network permission was denied or PO Token server failed to start.
        /// </summary>
        public async Task<bool> RetryYouTubeHelperSetupAsync()
        {
            if (!EnableSabrProxy)
            {
                return true;
            }

            if (Interlocked.CompareExchange(ref _youTubeSetupRunning, 1, 0) != 0)
            {
                return _bgutilServerReady;
            }

            try
            {
                OnStatusUpdate?.Invoke(this, "Retrying YouTube helper setup...");
                if (!await ResetYouTubeHelperAsync().ConfigureAwait(false))
                {
                    return false;
                }
                await EnsureDeno().ConfigureAwait(false);
                await EnsureFfmpeg().ConfigureAwait(false);
                await EnsurePotProvider().ConfigureAwait(false);
                await EnsureBgutilServerAsync().ConfigureAwait(false);
                return _bgutilServerReady;
            }
            finally
            {
                Interlocked.Exchange(ref _youTubeSetupRunning, 0);
            }
        }

        public static bool IsYouTubeSessionError(string? errorText)
        {
            if (string.IsNullOrEmpty(errorText)) return false;
            return errorText.Contains("The page needs to be reloaded", StringComparison.OrdinalIgnoreCase)
                || errorText.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string[]?> TryResolveSabrLocalPlayUrl(SabrSession session)
        {
            if (session.Failed && GetSabrOutputLength(session) == 0)
            {
                return null;
            }

            bool ready = await Task.Run(() => WaitForSabrData(session));
            string? localPath = ResolveSabrPlayPathForVlc(session);
            if (localPath == null || !ready)
            {
                return null;
            }

            OnStatusUpdate?.Invoke(this, "SABR buffer ready. Playing from local file.");
            return new string[] { localPath };
        }

        /// <summary>
        /// Resolves the on-disk path VLC should open, handling temp→final rename races and failed mux cleanup.
        /// </summary>
        public string? ResolveSabrPlayPathForVlc(string? mediaPath)
        {
            if (string.IsNullOrEmpty(mediaPath) || !IsSabrLocalFile(mediaPath))
            {
                return mediaPath;
            }

            SabrSession? session = FindSabrSessionForPath(mediaPath);
            if (session == null)
            {
                return File.Exists(mediaPath) ? mediaPath : null;
            }

            return ResolveSabrPlayPathForVlc(session);
        }

        private static string? ResolveSabrPlayPathForVlc(SabrSession session)
        {
            const long minPlayableBytes = 262144;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                string? path = FindSabrOutputFile(session);
                if (path != null && File.Exists(path))
                {
                    long len = TryGetFileLength(path);
                    if (len >= minPlayableBytes)
                    {
                        if (session.Failed
                            && path.EndsWith(".temp.mkv", StringComparison.OrdinalIgnoreCase)
                            && !IsDownloadStillRunning(session))
                        {
                            string merged = GetSabrMergedOutputPath(session);
                            if (File.Exists(merged) && TryGetFileLength(merged) >= minPlayableBytes)
                            {
                                return merged;
                            }

                            return null;
                        }

                        return path;
                    }
                }

                if (session.Failed && !IsDownloadStillRunning(session))
                {
                    break;
                }

                Thread.Sleep(100);
            }

            return null;
        }

        /// <summary>
        /// Probes whether a YouTube URL is a live/upcoming broadcast vs a VOD.
        /// Returns true (live), false (confirmed VOD/replay), or null (probe failed).
        /// </summary>
        public async Task<bool?> ProbeYouTubeLiveBroadcastAsync(string url)
        {
            if (!IsYouTubeUrl(url))
            {
                return false;
            }

            if (IsYouTubeLiveUrl(url))
            {
                return true;
            }

            if (_youTubeLiveProbeCache.TryGetValue(url, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
            {
                return cached.IsLive;
            }

            bool? probed = await ProbeYouTubeLiveBroadcastCoreAsync(url).ConfigureAwait(false);
            _youTubeLiveProbeCache[url] = (probed, DateTime.UtcNow.AddMinutes(2));
            return probed;
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
                LastResolvedHttpHeaders = null;
                bool? youTubeLive = IsYouTubeUrl(url) ? await ProbeYouTubeLiveBroadcastAsync(url).ConfigureAwait(false) : false;

                if (EnableSabrProxy && IsYouTubeUrl(url))
                {
                    // SABR only works for VOD. Skip when live, upcoming, or probe failed.
                    if (youTubeLive == false)
                    {
                        if (_proxyPort == 0) StartProxyListener();
                        if (_proxyPort != 0)
                        {
                            for (int sabrAttempt = 0; sabrAttempt < 2; sabrAttempt++)
                            {
                                await EnsureBgutilServerAsync();
                                bool isLive = false;

                                SabrSession? existing = _sabrSessions.Values.FirstOrDefault(
                                    s => s.Url == url && !s.Failed && !s.IsLive);
                                if (existing != null)
                                {
                                    existing.LastAccessUtc = DateTime.UtcNow;
                                    string[]? localPaths = await TryResolveSabrLocalPlayUrl(existing);
                                    if (localPaths != null)
                                    {
                                        OnStatusUpdate?.Invoke(this, "SABR reusing active download.");
                                        return localPaths;
                                    }
                                }

                                ReleaseSabrSessions();
                                SabrSession session = StartSabrSession(url, isLive);
                                if (session.Failed)
                                {
                                    if (sabrAttempt == 0 && IsPoTokenProviderFailure(session.Error))
                                    {
                                        OnStatusUpdate?.Invoke(this, "PO Token server unreachable; restarting YouTube helper...");
                                        _bgutilServerReady = false;
                                        StopBgutilServerProcess();
                                        continue;
                                    }

                                    OnStatusUpdate?.Invoke(this, "SABR Proxy failed to start. Falling back to direct yt-dlp resolution.");
                                    break;
                                }

                                string[]? resolvedPaths = await TryResolveSabrLocalPlayUrl(session);
                                if (resolvedPaths != null)
                                {
                                    return resolvedPaths;
                                }

                                if (sabrAttempt == 0 && IsPoTokenProviderFailure(session.Error))
                                {
                                    OnStatusUpdate?.Invoke(this, "PO Token server unreachable; restarting YouTube helper...");
                                    _bgutilServerReady = false;
                                    StopBgutilServerProcess();
                                    continue;
                                }

                                OnStatusUpdate?.Invoke(this, "SABR Proxy failed to buffer. Falling back to direct yt-dlp resolution.");
                                break;
                            }
                        }
                    }
                    else if (youTubeLive == true)
                    {
                        OnStatusUpdate?.Invoke(this, "YouTube live stream detected — using direct playback.");
                    }
                }

                if (youTubeLive == true)
                {
                    string[]? liveUrls = await ResolveYouTubeLiveStreamUrls(url).ConfigureAwait(false);
                    if (liveUrls != null && liveUrls.Length > 0)
                    {
                        return liveUrls;
                    }
                }

                OnStatusUpdate?.Invoke(this, "Resolving stream URL...");

                string formatArg;
                if (youTubeLive == true)
                {
                    formatArg = _preferredMaxHeight > 0
                      ? $"best[height<={_preferredMaxHeight}][protocol*=m3u8_native]/best[height<={_preferredMaxHeight}][protocol*=m3u8]/b[height<={_preferredMaxHeight}]"
                      : "best[protocol*=m3u8_native]/best[protocol*=m3u8]/b";
                }
                else
                {
                    formatArg = _preferredMaxHeight > 0
                      ? $"bv[height<={_preferredMaxHeight}]+ba/b"
                      : "bv+ba/b";
                }

                string[]? streamUrls = await ResolveDirectStreamUrlsAsync(url, formatArg, youTubeLive == true);

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
        /// Fetches lightweight metadata without starting a full download.
        /// Used alongside SABR downloads so duration/title are available for seeking UI.
        /// </summary>
        public async Task<YtDlpMetadata?> GetLightMetadata(string url)
        {
            if (!IsAvailable()) return null;

            try
            {
                string result = await RunYtDlp(
                    $"--no-download --no-playlist --print title --print duration --print uploader --print filesize_approx \"{url}\"");
                if (string.IsNullOrWhiteSpace(result)) return null;

                var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) return null;

                double? duration = null;
                if (double.TryParse(lines[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double dur))
                {
                    duration = dur;
                }

                long? filesize = null;
                if (lines.Length > 3
                    && long.TryParse(lines[3].Trim(), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out long fs)
                    && fs > 0)
                {
                    filesize = fs;
                }

                return new YtDlpMetadata
                {
                    Title = lines[0].Trim(),
                    Duration = duration,
                    Uploader = lines.Length > 2 ? lines[2].Trim() : null,
                    FilesizeApprox = filesize,
                };
            }
            catch (Exception e)
            {
                OnError?.Invoke(this, e);
            }

            return null;
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

        private static string[]? ParseStreamUrls(string? result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return null;
            }

            string[] urls = result.Trim()
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(u => u.Trim())
                .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return urls.Length > 0 ? urls : null;
        }

        public static bool IsHlsStreamUrl(string? url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            string path = url.Split('?')[0];
            return path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || url.Contains("/manifest/hls", StringComparison.OrdinalIgnoreCase)
                || url.Contains("hls_playlist", StringComparison.OrdinalIgnoreCase)
                || url.Contains("playlist_type/live", StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, string> EnsureYouTubeHttpHeaders(
            Dictionary<string, string>? headers,
            string pageUrl)
        {
            headers ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!headers.ContainsKey("Referer"))
            {
                headers["Referer"] = IsYouTubeUrl(pageUrl) ? pageUrl : "https://www.youtube.com/";
            }

            if (!headers.ContainsKey("Origin"))
            {
                headers["Origin"] = "https://www.youtube.com";
            }

            if (!headers.ContainsKey("User-Agent"))
            {
                headers["User-Agent"] =
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
            }

            return headers;
        }

        public static Dictionary<string, string> MergeStreamHeaders(
            Dictionary<string, string>? primary,
            Dictionary<string, string>? secondary,
            string pageUrl)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (secondary != null)
            {
                foreach (var kvp in secondary)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            if (primary != null)
            {
                foreach (var kvp in primary)
                {
                    merged[kvp.Key] = kvp.Value;
                }
            }

            return IsYouTubeUrl(pageUrl) ? EnsureYouTubeHttpHeaders(merged, pageUrl) : merged;
        }

        /// <summary>
        /// Builds HTTP headers for VLC/proxy playback, including cookies from cookies.txt when needed.
        /// </summary>
        public Dictionary<string, string> BuildPlaybackHeaders(
            Dictionary<string, string>? primary,
            Dictionary<string, string>? secondary,
            string pageUrl)
        {
            var merged = MergeStreamHeaders(primary, secondary, pageUrl);
            if (!merged.ContainsKey("Cookie"))
            {
                string? cookieHeader = BuildCookieHeaderForStreaming();
                if (!string.IsNullOrEmpty(cookieHeader))
                {
                    merged["Cookie"] = cookieHeader;
                }
            }

            return merged;
        }

        /// <summary>
        /// Builds a Cookie header from the Netscape cookies.txt (YouTube / googlevideo domains).
        /// </summary>
        public string? BuildCookieHeaderForStreaming()
        {
            if (!HasCookies)
            {
                return null;
            }

            try
            {
                var pairs = new List<string>();
                foreach (string line in File.ReadAllLines(_cookiesPath!))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    string[] parts = line.Split('\t');
                    if (parts.Length < 7)
                    {
                        continue;
                    }

                    string domain = parts[0];
                    if (!domain.Contains("youtube", StringComparison.OrdinalIgnoreCase)
                        && !domain.Contains("googlevideo", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (long.TryParse(parts[4], out long expiry) && expiry > 0 && expiry < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                    {
                        continue;
                    }

                    string name = parts[5];
                    string value = parts[6];
                    if (!string.IsNullOrEmpty(name))
                    {
                        pairs.Add($"{name}={value}");
                    }
                }

                return pairs.Count > 0 ? string.Join("; ", pairs) : null;
            }
            catch
            {
                return null;
            }
        }

        private static YtDlpMetadata? TryParseYtDlpJson(string? result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return null;
            }

            string? jsonLine = result
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.TrimStart().StartsWith("{"));
            return jsonLine == null ? null : JsonConvert.DeserializeObject<YtDlpMetadata>(jsonLine);
        }

        private async Task<string[]?> ResolveYouTubeLiveStreamUrls(string url)
        {
            string heightFilter = _preferredMaxHeight > 0 ? $"[height<={_preferredMaxHeight}]" : "";
            string[] formatAttempts =
            {
                $"best{heightFilter}[protocol*=m3u8_native]/best{heightFilter}[protocol*=m3u8]",
                $"96{heightFilter}/b{heightFilter}",
                $"b{heightFilter}[protocol*=m3u8]/b{heightFilter}",
            };

            foreach (string formatArg in formatAttempts)
            {
                try
                {
                    string result = await RunYtDlp(
                        $"--no-playlist -j -f \"{formatArg}\" \"{url}\"",
                        isLiveYouTube: true);
                    YtDlpMetadata? info = TryParseYtDlpJson(result);
                    string? playbackUrl = info?.PlaybackUrl;
                    if (info == null || string.IsNullOrEmpty(playbackUrl))
                    {
                        continue;
                    }

                    if (!IsHlsStreamUrl(playbackUrl))
                    {
                        continue;
                    }

                    LastResolvedHttpHeaders = info.HttpHeaders;
                    OnStatusUpdate?.Invoke(this, "YouTube live HLS URL resolved.");
                    return new[] { playbackUrl };
                }
                catch (Exception e)
                {
                    OnError?.Invoke(this, e);
                }
            }

            return null;
        }

        private async Task<string> RunYtDlp(
            string arguments,
            bool withCommonArgs = true,
            bool isLiveYouTube = false,
            string? forceYouTubeClient = null)
        {
            return await Task.Run(() =>
            {
                // Inject cookies if available (e.g. from VRCVideoCacher browser extension)
                string fullArgs = (withCommonArgs ? BuildCommonArgs(isLiveYouTube, forceYouTubeClient) : "") + arguments;
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
        /// 1. Plugin directory (cookies.txt, user-provided or auto-saved from clipboard)
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

        /// <summary>Netscape cookies.txt path used for yt-dlp and Twitch viewer telemetry.</summary>
        public string? CookiesFilePath => FindCookiesFile();

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

        private async Task<string[]?> ResolveDirectStreamUrlsAsync(string url, string formatArg, bool isLiveYouTube)
        {
            string[]? streamUrls = await TryGetStreamUrlsAsync(url, formatArg, isLiveYouTube, forceYouTubeClient: null);
            if (streamUrls != null && streamUrls.Length > 0)
            {
                return streamUrls;
            }

            if (!IsYouTubeUrl(url) || isLiveYouTube)
            {
                return null;
            }

            OnStatusUpdate?.Invoke(this, "Retrying stream resolution with YouTube tv client...");
            return await TryGetStreamUrlsAsync(url, formatArg, isLiveYouTube: false, forceYouTubeClient: "tv");
        }

        private async Task<string[]?> TryGetStreamUrlsAsync(
            string url,
            string formatArg,
            bool isLiveYouTube,
            string? forceYouTubeClient)
        {
            try
            {
                string result = await RunYtDlp(
                    $"--no-playlist --get-url -f \"{formatArg}\" \"{url}\"",
                    isLiveYouTube: isLiveYouTube,
                    forceYouTubeClient: forceYouTubeClient);
                string[]? streamUrls = ParseStreamUrls(result);
                if (streamUrls == null || streamUrls.Length == 0)
                {
                    return null;
                }

                await TryCaptureFormatHttpHeadersAsync(url, formatArg, isLiveYouTube, forceYouTubeClient);
                return streamUrls;
            }
            catch (Exception e)
            {
                OnError?.Invoke(this, e);
                return null;
            }
        }

        private async Task TryCaptureFormatHttpHeadersAsync(
            string url,
            string formatArg,
            bool isLiveYouTube,
            string? forceYouTubeClient)
        {
            try
            {
                string result = await RunYtDlp(
                    $"--no-playlist -j -f \"{formatArg}\" \"{url}\"",
                    isLiveYouTube: isLiveYouTube,
                    forceYouTubeClient: forceYouTubeClient);
                YtDlpMetadata? info = TryParseYtDlpJson(result);
                if (info?.HttpHeaders != null && info.HttpHeaders.Count > 0)
                {
                    LastResolvedHttpHeaders = info.HttpHeaders;
                }
            }
            catch
            {
                // Non-fatal; BuildPlaybackHeaders still adds cookies and YouTube defaults.
            }
        }

        private string PotProviderPluginPath =>
            Path.Combine(PluginsDir, "yt_dlp_plugins", "extractor", "getpot_bgutil_http.py");

        private bool IsPotProviderPluginInstalled() => File.Exists(PotProviderPluginPath);

        private string BuildPotProviderExtractorArgs()
        {
            if (!_bgutilServerReady || !IsPotProviderPluginInstalled())
            {
                return string.Empty;
            }

            return "--extractor-args \"youtubepot-bgutilhttp:base_url=http://127.0.0.1:4416\" ";
        }

        /// <summary>
        /// Builds the common argument prefix (cookies, etc.) for all yt-dlp calls.
        /// </summary>
        private string BuildCommonArgs(bool isLiveYouTube = false, string? forceYouTubeClient = null)
        {
            string args = $"--extractor-args \"{BuildYouTubeExtractorArgs(isLive: isLiveYouTube, includeSabrFormats: false, forceClient: forceYouTubeClient)}\" --extractor-args \"youtubetab:skip=authcheck\" ";
            args += BuildPotProviderExtractorArgs();

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
        private string BgutilReadyMarker => Path.Combine(BgutilServerWorkDir, ".xivmp-deno-ready-v2");

        private bool HasYouTubeAuth => HasCookies || !string.IsNullOrEmpty(CookieBrowser);

        private string BuildYouTubeExtractorArgs(bool isLive, bool includeSabrFormats, string? forceClient = null)
        {
            // web/mweb need PO tokens from bgutil; tv works with cookies without tokens.
            // android often exposes m3u8_native for YouTube live streams.
            string clients = forceClient ?? (isLive && !includeSabrFormats
                ? (HasYouTubeAuth
                    ? (_bgutilServerReady ? "android,web,tv" : "android,tv")
                    : "android,tv,web_embedded")
                : (HasYouTubeAuth
                    ? (_bgutilServerReady ? "web,mweb,tv" : "tv")
                    : (_bgutilServerReady ? "tv,mweb,web_embedded" : "tv,web_embedded")));

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

        /// <summary>Fast URL-shape check for /live/ links. Does not detect watch?v= live streams.</summary>
        public static bool IsYouTubeLiveUrlHeuristic(string url)
            => IsYouTubeLiveUrl(url);

        private static bool IsYouTubeLiveUrl(string url)
        {
            return url.Contains("youtube.com/live/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("youtu.be/live/", StringComparison.OrdinalIgnoreCase)
                || (url.Contains("/live", StringComparison.OrdinalIgnoreCase)
                    && url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ParseYouTubeLiveProbeLines(IReadOnlyList<string> lines, out bool isLiveBroadcast)
        {
            isLiveBroadcast = false;
            if (lines.Count == 0)
            {
                return false;
            }

            string liveStatus = lines[0].Trim();
            if (!string.IsNullOrEmpty(liveStatus))
            {
                if (string.Equals(liveStatus, "is_live", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(liveStatus, "is_upcoming", StringComparison.OrdinalIgnoreCase))
                {
                    isLiveBroadcast = true;
                    return true;
                }

                if (string.Equals(liveStatus, "not_live", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(liveStatus, "was_live", StringComparison.OrdinalIgnoreCase))
                {
                    isLiveBroadcast = false;
                    return true;
                }
            }

            if (lines.Count > 1)
            {
                string isLiveValue = lines[1].Trim();
                if (bool.TryParse(isLiveValue, out bool parsed))
                {
                    isLiveBroadcast = parsed;
                    return true;
                }

                if (isLiveValue == "1")
                {
                    isLiveBroadcast = true;
                    return true;
                }

                if (isLiveValue == "0")
                {
                    isLiveBroadcast = false;
                    return true;
                }
            }

            return false;
        }

        private async Task<bool?> ProbeYouTubeLiveBroadcastCoreAsync(string url)
        {
            if (!IsAvailable())
            {
                return null;
            }

            try
            {
                OnStatusUpdate?.Invoke(this, "Checking if YouTube link is live...");
                string result = await RunYtDlp(
                    $"--no-download --no-playlist --print live_status --print is_live \"{url}\"");
                var lines = result
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l))
                    .ToArray();

                if (ParseYouTubeLiveProbeLines(lines, out bool isLiveBroadcast))
                {
                    return isLiveBroadcast;
                }
            }
            catch (Exception e)
            {
                OnError?.Invoke(this, e);
            }

            return null;
        }

        private string DenoExecutablePath => Path.Combine(PluginDir, "deno.exe");

        private string BuildSabrFormatSelector()
        {
            string heightFilter = _preferredMaxHeight > 0 ? $"[height<={_preferredMaxHeight}]" : "";
            if (!_bgutilServerReady)
            {
                // Without PO tokens, SABR/web formats fail — use DASH merge via tv client only.
                return $"bv{heightFilter}+ba/b";
            }

            // Prefer standard DASH merge (works with PO tokens), then SABR merge, then single-file fallback.
            return $"bv{heightFilter}+ba/ba[protocol=sabr]+bv[protocol=sabr]{heightFilter}/b";
        }

        private string BuildSabrDownloadArgs(string url, bool isLive, string outputTemplate)
        {
            string args = "--extractor-args \"youtubetab:skip=authcheck\" ";
            args += $"--extractor-args \"{BuildSabrYouTubeExtractorArgs(isLive)}\" ";
            args += BuildPotProviderExtractorArgs();

            if (Directory.Exists(PluginsDir))
            {
                args += $"--plugin-dirs {QuotedYtDlpPath(PluginsDir)} ";
            }

            if (File.Exists(DenoExecutablePath))
            {
                args += $"--js-runtimes deno:{QuotedYtDlpPath(DenoExecutablePath)} ";
            }

            args += "--remote-components ejs:github --socket-timeout 30 --no-part --merge-output-format mkv --embed-metadata ";
            args += "--retries 10 --fragment-retries 20 --skip-unavailable-fragments ";

            // SABR merged formats cannot be partially downloaded (--download-sections aborts).
            // Resume is handled by downloading from the start and deferred seek in MediaObject.

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

        private static string GetSabrMergedOutputPath(SabrSession session)
            => Path.Combine(session.TempDir, "stream.mkv");

        private static string GetSabrTempOutputPath(SabrSession session)
            => Path.Combine(session.TempDir, "stream.temp.mkv");

        private static bool IsBenignSabrFinalizeError(string text)
        {
            return text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                && text.Contains("stream.temp.mkv", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryParseDownloadPercent(string line, SabrSession session)
        {
            if (!line.Contains("[download]", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            int pctIdx = line.IndexOf('%');
            if (pctIdx > 0)
            {
                int start = pctIdx - 1;
                while (start >= 0 && (char.IsDigit(line[start]) || line[start] == '.'))
                {
                    start--;
                }

                if (start < pctIdx - 1)
                {
                    ReadOnlySpan<char> numSpan = line.AsSpan(start + 1, pctIdx - start - 1).Trim();
                    if (float.TryParse(numSpan, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float pct))
                    {
                        session.DownloadPercent = Math.Clamp(pct / 100f, 0f, 1f);
                    }
                }
            }

            int ofIdx = line.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);
            if (ofIdx < 0)
            {
                return;
            }

            ReadOnlySpan<char> sizeSpan = line.AsSpan(ofIdx + 4).TrimStart();
            if (sizeSpan.StartsWith("~"))
            {
                sizeSpan = sizeSpan.Slice(1).TrimStart();
            }

            int end = 0;
            while (end < sizeSpan.Length && !char.IsWhiteSpace(sizeSpan[end]))
            {
                end++;
            }

            if (end > 0 && TryParseByteSize(sizeSpan.Slice(0, end), out long totalBytes))
            {
                session.EstimatedFinalBytes = Math.Max(session.EstimatedFinalBytes, totalBytes);
            }
        }

        private static bool TryParseByteSize(ReadOnlySpan<char> token, out long bytes)
        {
            bytes = 0;
            if (token.IsEmpty)
            {
                return false;
            }

            int unitStart = token.Length - 1;
            while (unitStart >= 0 && char.IsLetter(token[unitStart]))
            {
                unitStart--;
            }

            if (unitStart >= token.Length - 1)
            {
                return false;
            }

            ReadOnlySpan<char> numberPart = token.Slice(0, unitStart + 1);
            ReadOnlySpan<char> unitPart = token.Slice(unitStart + 1);

            if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                return false;
            }

            double multiplier = 1d;
            if (unitPart.Equals("KiB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1024d;
            else if (unitPart.Equals("MiB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1024d * 1024d;
            else if (unitPart.Equals("GiB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1024d * 1024d * 1024d;
            else if (unitPart.Equals("KB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1000d;
            else if (unitPart.Equals("MB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1000d * 1000d;
            else if (unitPart.Equals("GB".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1000d * 1000d * 1000d;
            else if (unitPart.Equals("B".AsSpan(), StringComparison.OrdinalIgnoreCase)) multiplier = 1d;
            else return false;

            bytes = (long)(value * multiplier);
            return bytes > 0;
        }

        private static string? FindSabrOutputFile(SabrSession session)
        {
            string merged = GetSabrMergedOutputPath(session);
            string temp = GetSabrTempOutputPath(session);

            long mergedLen = File.Exists(merged) ? TryGetFileLength(merged) : 0;
            long tempLen = File.Exists(temp) ? TryGetFileLength(temp) : 0;

            if (tempLen > mergedLen) return tempLen > 0 ? temp : null;
            if (mergedLen > 0) return merged;
            if (tempLen > 0) return temp;
            if (File.Exists(merged)) return merged;
            if (File.Exists(temp)) return temp;
            return null;
        }

        private static long TryGetFileLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        private static long GetSabrOutputLength(SabrSession session)
        {
            string? path = FindSabrOutputFile(session);
            if (path == null) return 0;
            session.TempPath = path;
            return TryGetFileLength(path);
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

            if (_expectedFilesizeByUrl.TryGetValue(url, out long expectedBytes) && expectedBytes > 0)
            {
                session.EstimatedFinalBytes = expectedBytes;
            }

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
            ReportSabrBufferProgress(session, force: true);

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                stderr.AppendLine(e.Data);
                if (IsBenignSabrFinalizeError(e.Data)) return;
                TryParseDownloadPercent(e.Data, session);
                ReportSabrBufferProgress(session);
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
                    long finalBytes = GetSabrOutputLength(session);
                    if (finalBytes > 0)
                    {
                        session.EstimatedFinalBytes = Math.Max(session.EstimatedFinalBytes, finalBytes);
                    }

                    if (session.DownloadPercent < 0f && finalBytes > 0)
                    {
                        session.DownloadPercent = 1f;
                    }
                    if (process.HasExited && process.ExitCode != 0)
                    {
                        string err = stderr.ToString();
                        long outputLen = GetSabrOutputLength(session);
                        if ((IsBenignSabrFinalizeError(err) || outputLen >= 262144) && outputLen > 0)
                        {
                            OnStatusUpdate?.Invoke(this,
                                $"SABR download ended (exit {process.ExitCode}); continuing with {outputLen / (1024 * 1024.0):0.#}MB buffered.");
                            return;
                        }

                        session.Failed = true;
                        session.Error = err.Length > 0 ? err : $"yt-dlp exited with code {process.ExitCode}";
                        OnError?.Invoke(this, new Exception($"SABR download failed: {session.Error}"));
                    }
                }
                finally
                {
                    try { process.Dispose(); } catch { }
                    session.Process = null;
                }
            });

            return session;
        }

        private void CleanupSabrSession(SabrSession session)
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

            if (!TryDeleteSabrTempDir(session.TempDir))
            {
                QueueSabrTempDirDelete(session.TempDir);
            }
        }

        private void QueueSabrTempDirDelete(string tempDir)
        {
            if (!string.IsNullOrEmpty(tempDir))
            {
                _pendingSabrDirDeletes.Enqueue(tempDir);
            }
        }

        private void ProcessPendingSabrDirDeletes()
        {
            int pending = _pendingSabrDirDeletes.Count;
            for (int i = 0; i < pending; i++)
            {
                if (!_pendingSabrDirDeletes.TryDequeue(out string? dir))
                {
                    break;
                }

                if (!TryDeleteSabrTempDir(dir))
                {
                    _pendingSabrDirDeletes.Enqueue(dir);
                }
            }
        }

        private static bool TryDeleteSabrTempDir(string? tempDir, int retries = 5)
        {
            if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
            {
                return true;
            }

            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    Directory.Delete(tempDir, true);
                    return true;
                }
                catch (IOException) when (attempt < retries - 1)
                {
                    Thread.Sleep(150 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < retries - 1)
                {
                    Thread.Sleep(150 * (attempt + 1));
                }
                catch
                {
                    break;
                }
            }

            return !Directory.Exists(tempDir);
        }

        private static long GetDirectorySizeBytes(string dir)
        {
            long size = 0;
            try
            {
                foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        size += new FileInfo(file).Length;
                    }
                    catch { }
                }
            }
            catch { }

            return size;
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

        private bool WaitForSabrData(SabrSession session, long requiredOffset = 0, int timeoutMs = 300000)
        {
            const long minMergedBytes = 262144; // wait for muxed mkv, not a video-only fragment
            long needBytes = requiredOffset > 0 ? requiredOffset + 1 : minMergedBytes;
            var deadline = Environment.TickCount64 + timeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                long len = GetSabrOutputLength(session);
                ReportSabrBufferProgress(session);

                if (session.Failed)
                {
                    return len >= needBytes;
                }

                if (len >= needBytes) return true;

                if (session.Process?.HasExited == true || session.DownloadFinished)
                {
                    if (len >= needBytes) return true;
                    if (requiredOffset > 0) return len > requiredOffset;
                    return len >= minMergedBytes && !session.Failed;
                }

                Thread.Sleep(100);
            }

            ReportSabrBufferProgress(session, force: true);
            long finalLen = GetSabrOutputLength(session);
            if (requiredOffset > 0)
            {
                return finalLen > requiredOffset;
            }

            return finalLen >= minMergedBytes && !session.Failed;
        }

        private static bool TryParseRangeHeader(string? rangeHeader, out long start, out long? end)
        {
            start = 0;
            end = null;
            if (string.IsNullOrEmpty(rangeHeader)
                || !rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string spec = rangeHeader["bytes=".Length..].Trim();
            int dash = spec.IndexOf('-');
            if (dash < 0) return false;

            string startPart = spec[..dash];
            string endPart = spec[(dash + 1)..];

            if (!string.IsNullOrEmpty(startPart))
            {
                if (!long.TryParse(startPart, out start) || start < 0) return false;
            }
            else
            {
                return false; // suffix ranges not supported
            }

            if (!string.IsNullOrEmpty(endPart))
            {
                if (!long.TryParse(endPart, out long parsedEnd) || parsedEnd < start) return false;
                end = parsedEnd;
            }

            return true;
        }

        private bool WaitForSabrBytes(SabrSession session, long requiredOffset, int timeoutMs = 300000)
        {
            return WaitForSabrData(session, requiredOffset, timeoutMs);
        }

        /// <summary>
        /// For HTTP/1.0 clients, batch more data per connection before responding so VLC
        /// has enough to play between reconnects.
        /// </summary>
        private static void WaitForSabrGrowthBatch(SabrSession session, long offset)
        {
            const long minBatchBytes = 4 * 1024 * 1024;
            const int maxWaitMs = 4000;
            const int stallMs = 400;

            long batchTarget = offset + minBatchBytes;
            var deadline = Environment.TickCount64 + maxWaitMs;
            long lastLen = GetSabrOutputLength(session);
            long lastGrowthTick = Environment.TickCount64;

            while (Environment.TickCount64 < deadline)
            {
                if (session.Failed) return;

                long len = GetSabrOutputLength(session);
                if (len >= batchTarget) return;
                if (session.DownloadFinished && !IsDownloadStillRunning(session)) return;

                if (len > lastLen)
                {
                    lastLen = len;
                    lastGrowthTick = Environment.TickCount64;
                }
                else if (len > offset && Environment.TickCount64 - lastGrowthTick >= stallMs)
                {
                    return;
                }

                Thread.Sleep(50);
            }
        }

        private void StreamSabrFileToResponse(SabrSession session, HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            response.AddHeader("Accept-Ranges", "bytes");
            response.ContentType = "video/x-matroska";
            response.AddHeader("Cache-Control", "no-cache");

            // VLC and many media players use HTTP/1.0, which cannot use chunked transfer encoding.
            bool supportsChunked = request.ProtocolVersion >= HttpVersion.Version11;

            bool hasRange = TryParseRangeHeader(request.Headers["Range"], out long rangeStart, out long? rangeEnd);
            long offset = hasRange ? rangeStart : 0;

            if (string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
            {
                long headLength = GetSabrOutputLength(session);
                bool complete = session.DownloadFinished && !IsDownloadStillRunning(session);
                if (complete && headLength > 0)
                {
                    response.ContentLength64 = headLength;
                }

                response.StatusCode = 200;
                return;
            }

            if (hasRange && !WaitForSabrBytes(session, offset, timeoutMs: 60000))
            {
                response.StatusCode = 416;
                response.StatusDescription = "Range Not Satisfiable";
                return;
            }

            if (!supportsChunked && !hasRange)
            {
                WaitForSabrGrowthBatch(session, offset);
            }

            bool downloadComplete = session.DownloadFinished && !IsDownloadStillRunning(session);
            long fileLength = GetSabrOutputLength(session);

            // For HTTP/1.0 or when total size is known, use Content-Length instead of chunked encoding.
            // Clients reconnect with Range to fetch newly downloaded bytes.
            long? sendLimit = null;

            if (hasRange)
            {
                response.StatusCode = 206;
                long endPos = rangeEnd.HasValue
                    ? Math.Min(rangeEnd.Value, Math.Max(offset, fileLength - 1))
                    : Math.Max(offset, fileLength - 1);

                if (downloadComplete && fileLength > 0)
                {
                    endPos = rangeEnd.HasValue ? Math.Min(rangeEnd.Value, fileLength - 1) : fileLength - 1;
                    response.Headers["Content-Range"] = $"bytes {offset}-{fileLength - 1}/{fileLength}";
                    sendLimit = endPos - offset + 1;
                }
                else if (supportsChunked && !rangeEnd.HasValue)
                {
                    response.Headers["Content-Range"] = $"bytes {offset}-/*";
                    response.SendChunked = true;
                }
                else
                {
                    response.Headers["Content-Range"] = $"bytes {offset}-{endPos}/*";
                    sendLimit = Math.Max(0, endPos - offset + 1);
                }
            }
            else if (downloadComplete && fileLength > 0)
            {
                sendLimit = fileLength - offset;
            }
            else if (supportsChunked)
            {
                response.SendChunked = true;
            }
            else
            {
                sendLimit = Math.Max(0, fileLength - offset);
            }

            if (sendLimit.HasValue)
            {
                response.ContentLength64 = sendLimit.Value;
                response.SendChunked = false;
            }

            var buffer = new byte[128 * 1024];
            string? openPath = null;
            long bytesSent = 0;

            while (true)
            {
                if (sendLimit.HasValue && bytesSent >= sendLimit.Value)
                {
                    break;
                }

                string? outputPath = FindSabrOutputFile(session);
                if (outputPath == null)
                {
                    if (session.Failed && GetSabrOutputLength(session) == 0) break;
                    if (!IsDownloadStillRunning(session)) break;
                    Thread.Sleep(50);
                    continue;
                }

                session.TempPath = outputPath;
                if (openPath != null && openPath != outputPath)
                {
                    openPath = outputPath;
                }
                else if (openPath == null)
                {
                    openPath = outputPath;
                }

                fileLength = TryGetFileLength(outputPath);
                downloadComplete = session.DownloadFinished && !IsDownloadStillRunning(session);

                if (offset >= fileLength)
                {
                    if (sendLimit.HasValue) break;
                    if (!IsDownloadStillRunning(session)) break;
                    if (session.Failed) break;
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    using var fs = new FileStream(
                        outputPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);

                    if (offset > fs.Length)
                    {
                        if (sendLimit.HasValue) break;
                        if (!IsDownloadStillRunning(session)) break;
                        Thread.Sleep(50);
                        continue;
                    }

                    fs.Seek(offset, SeekOrigin.Begin);

                    long bytesRemaining = sendLimit.HasValue
                        ? sendLimit.Value - bytesSent
                        : long.MaxValue;

                    if (rangeEnd.HasValue && sendLimit.HasValue)
                    {
                        bytesRemaining = Math.Min(bytesRemaining, rangeEnd.Value - offset + 1 - bytesSent);
                    }
                    else if (downloadComplete && fileLength > 0 && sendLimit.HasValue)
                    {
                        bytesRemaining = Math.Min(bytesRemaining, fileLength - offset - bytesSent);
                    }

                    int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    if (toRead <= 0) break;

                    int read = fs.Read(buffer, 0, toRead);
                    if (read > 0)
                    {
                        try
                        {
                            response.OutputStream.Write(buffer, 0, read);
                            response.OutputStream.Flush();
                        }
                        catch (Exception ex) when (IsClientDisconnect(ex))
                        {
                            break;
                        }

                        offset += read;
                        bytesSent += read;
                        continue;
                    }
                }
                catch (IOException)
                {
                    if (sendLimit.HasValue) break;
                    if (!IsDownloadStillRunning(session)) break;
                    Thread.Sleep(50);
                    continue;
                }

                if (sendLimit.HasValue) break;

                if (!IsDownloadStillRunning(session))
                {
                    break;
                }

                if (session.Failed && GetSabrOutputLength(session) == 0) break;
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

                if (session.Failed && GetSabrOutputLength(session) == 0)
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
                session.LastAccessUtc = DateTime.UtcNow;
                Interlocked.Increment(ref session.StreamConsumers);

                bool hasRange = TryParseRangeHeader(context.Request.Headers["Range"], out long rangeStart, out _);
                long waitOffset = hasRange ? rangeStart : 0;
                if (!WaitForSabrData(session, waitOffset))
                {
                    context.Response.StatusCode = 502;
                    context.Response.StatusDescription = "SABR stream unavailable";
                    context.Response.Close();
                    long buffered = GetSabrOutputLength(session);
                    string detail = session.Error
                        ?? (IsDownloadStillRunning(session)
                            ? $"Still buffering (offset {waitOffset}, {buffered / (1024 * 1024.0):0.#}MB ready)."
                            : "Timed out waiting for SABR stream data.");
                    if (buffered == 0 || !IsDownloadStillRunning(session))
                    {
                        OnError?.Invoke(this, new Exception($"SABR proxy stream unavailable: {detail}"));
                    }
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

                if (session != null)
                {
                    session.LastAccessUtc = DateTime.UtcNow;
                    Interlocked.Decrement(ref session.StreamConsumers);
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
            if (File.Exists(marker) && IsPotProviderPluginInstalled())
            {
                return;
            }

            if (File.Exists(marker))
            {
                try { File.Delete(marker); } catch { }
            }

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

        private static bool IsPoTokenProviderFailure(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            return text.Contains("127.0.0.1:4416", StringComparison.OrdinalIgnoreCase)
                || text.Contains("pot:bgutil:http", StringComparison.OrdinalIgnoreCase)
                || text.Contains("GVS PO Token", StringComparison.OrdinalIgnoreCase)
                || text.Contains("requires a GVS PO Token", StringComparison.OrdinalIgnoreCase);
        }

        private async Task EnsureBgutilServerAsync()
        {
            if (!EnableSabrProxy) return;

            await _bgutilServerGate.WaitAsync();
            try
            {
                if (_bgutilServerReady)
                {
                    if (await IsBgutilServerRespondingAsync() && !TryGetBgutilProcessExitReason(out _))
                    {
                        return;
                    }

                    _bgutilServerReady = false;
                    StopBgutilServerProcess();
                    OnStatusUpdate?.Invoke(this, "PO Token server was offline; restarting...");
                }

                await CleanupStaleBgutilInstallDirsAsync().ConfigureAwait(false);
                await EnsurePotProvider().ConfigureAwait(false);

                if (!File.Exists(DenoExecutablePath))
                {
                    OnError?.Invoke(this, new Exception("Deno is required for the PO Token provider but was not found."));
                    return;
                }

                if (!IsPotProviderPluginInstalled())
                {
                    OnStatusUpdate?.Invoke(this, "PO Token provider plugin unavailable; using tv client without PO tokens.");
                    return;
                }

                await DownloadBgutilServerIfNeededAsync();
                EnsureBgutilServerBindPatch();
                if (!await SetupBgutilServerDepsAsync())
                {
                    OnStatusUpdate?.Invoke(this, "PO Token provider unavailable; using tv client without PO tokens.");
                    return;
                }

                KillPluginDenoProcesses();
                KillListenersOnPort(4416);
                await Task.Delay(500).ConfigureAwait(false);

                StartBgutilServerProcess();
                if (await WaitForBgutilServerReadyAsync())
                {
                    _bgutilServerReady = true;
                    OnStatusUpdate?.Invoke(this, "PO Token provider server ready on port 4416.");
                }
                else
                {
                    string detail = GetBgutilServerLogExcerpt();
                    OnError?.Invoke(this, new Exception(
                        "PO Token provider server failed to start on http://127.0.0.1:4416. " +
                        "Open Media Player Settings → Sources and click Fix YouTube setup. " +
                        "If Windows asks to allow internet access, click Allow. " +
                        (string.IsNullOrWhiteSpace(detail) ? "" : detail)));
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

            EnsureBgutilServerBindPatch();
        }

        /// <summary>
        /// bgutil defaults to [::] which on Windows often does not accept 127.0.0.1 connections.
        /// yt-dlp's PO provider plugin hardcodes http://127.0.0.1:4416 — bind there instead.
        /// </summary>
        private void EnsureBgutilServerBindPatch()
        {
            string mainTs = Path.Combine(BgutilServerWorkDir, "src", "main.ts");
            if (!File.Exists(mainTs))
            {
                return;
            }

            string content = File.ReadAllText(mainTs);
            if (!content.Contains("host: \"::\"", StringComparison.Ordinal))
            {
                return;
            }

            content = content.Replace("host: \"::\"", "host: \"127.0.0.1\"");
            content = content.Replace("host: \"0.0.0.0\"", "host: \"127.0.0.1\"");
            content = content.Replace("on on address [::]:", "on address 127.0.0.1:");
            content = content.Replace("on address [::]:", "on address 127.0.0.1:");
            content = content.Replace("on address 0.0.0.0:", "on address 127.0.0.1:");
            content = content.Replace("Could not listen on [::]:", "Could not listen on 127.0.0.1:");
            File.WriteAllText(mainTs, content);
        }

        private async Task<bool> SetupBgutilServerDepsAsync()
        {
            if (Directory.Exists(BgutilNodeModulesDir))
            {
                if (File.Exists(BgutilReadyMarker))
                {
                    return true;
                }

                // Partial install (e.g. user denied Deno network permission) — remove and retry.
                KillPluginDenoProcesses();
                if (!await TryDeleteDirectoryRobustAsync(BgutilNodeModulesDir).ConfigureAwait(false))
                {
                    OnError?.Invoke(this, CreateBgutilResetFailedException(
                        new IOException($"Access denied while deleting {BgutilNodeModulesDir}")));
                    return false;
                }
            }

            if (!Directory.Exists(BgutilServerWorkDir))
            {
                OnError?.Invoke(this, new Exception($"PO Token provider server directory not found: {BgutilServerWorkDir}"));
                return false;
            }

            OnStatusUpdate?.Invoke(this, "Setting up PO Token provider (first run may take 1-2 minutes)...");
            var (ok, output) = await RunProcessAsync(
                DenoExecutablePath,
                "install --allow-scripts=npm:canvas --frozen",
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
                    if (!_bgutilServerProcess.HasExited)
                    {
                        _bgutilServerProcess.Kill(true);
                    }
                }
                catch { }
                _bgutilServerProcess.Dispose();
                _bgutilServerProcess = null;
            }

            _bgutilServerLog.Clear();

            var psi = new ProcessStartInfo
            {
                FileName = DenoExecutablePath,
                Arguments = "run --allow-env --allow-net --allow-ffi=. --allow-read=. ../src/main.ts -p 4416",
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

            _bgutilServerProcess.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                AppendBgutilServerLog(e.Data);
            };
            _bgutilServerProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                AppendBgutilServerLog(e.Data);
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

        private void AppendBgutilServerLog(string line)
        {
            lock (_bgutilServerLog)
            {
                if (_bgutilServerLog.Length > 0)
                {
                    _bgutilServerLog.AppendLine();
                }

                _bgutilServerLog.Append(line);
                if (_bgutilServerLog.Length > 8000)
                {
                    _bgutilServerLog.Remove(0, _bgutilServerLog.Length - 8000);
                }
            }
        }

        private string GetBgutilServerLogExcerpt()
        {
            lock (_bgutilServerLog)
            {
                if (_bgutilServerLog.Length == 0)
                {
                    return string.Empty;
                }

                string text = _bgutilServerLog.ToString().Trim();
                if (text.Length > 600)
                {
                    text = text[^600..];
                }

                return "Server log: " + text;
            }
        }

        private bool TryGetBgutilProcessExitReason(out string reason)
        {
            reason = string.Empty;
            if (_bgutilServerProcess == null)
            {
                return false;
            }

            try
            {
                if (_bgutilServerProcess.HasExited)
                {
                    reason = $"PO Token server process exited with code {_bgutilServerProcess.ExitCode}.";
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        private const string BgutilPingUrl = "http://127.0.0.1:4416/ping";

        /// <summary>
        /// yt-dlp hardcodes 127.0.0.1 for the bgutil HTTP provider — do not use localhost/[::1] here.
        /// </summary>
        private static async Task<bool> IsBgutilServerRespondingAsync()
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            try
            {
                using var response = await client.GetAsync(BgutilPingUrl);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        private static void KillListenersOnPort(int port)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netstat",
                    Arguments = "-ano",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                using var netstat = Process.Start(psi);
                if (netstat == null)
                {
                    return;
                }

                string output = netstat.StandardOutput.ReadToEnd();
                netstat.WaitForExit(5000);

                string portToken = $":{port}";
                var pids = new HashSet<int>();
                foreach (string line in output.Split('\n'))
                {
                    if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)
                        || !line.Contains(portToken, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && int.TryParse(parts[^1], out int pid) && pid > 0)
                    {
                        pids.Add(pid);
                    }
                }

                foreach (int pid in pids)
                {
                    try
                    {
                        using Process process = Process.GetProcessById(pid);
                        if (process.HasExited)
                        {
                            continue;
                        }

                        process.Kill(true);
                        process.WaitForExit(3000);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private async Task<bool> WaitForBgutilServerReadyAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (TryGetBgutilProcessExitReason(out string exitReason))
                {
                    AppendBgutilServerLog(exitReason);
                    return false;
                }

                if (await IsBgutilServerRespondingAsync())
                {
                    return true;
                }

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
