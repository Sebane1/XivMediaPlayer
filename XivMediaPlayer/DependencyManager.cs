using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer
{
    public class DependencyManager
    {
        private readonly IPluginLog _pluginLog;
        private readonly string _version;
        
        public bool IsReady { get; private set; }
        public bool IsDownloading { get; private set; }
        public float DownloadProgress { get; private set; }
        public string Status { get; private set; } = "Initializing...";
        public bool HasError { get; private set; }
        public string ErrorMessage { get; private set; } = string.Empty;

        public string DependenciesDir { get; private set; }

        public DependencyManager(string configDir, string pluginDir, string version, IPluginLog pluginLog)
        {
            _version = version;
            _pluginLog = pluginLog;

            // Check if dependencies exist in the plugin folder (e.g. for local developer compiles)
            if (Directory.Exists(Path.Combine(pluginDir, "cef")) && Directory.Exists(Path.Combine(pluginDir, "libvlc")))
            {
                DependenciesDir = pluginDir;
                CheckDependencies();
                if (IsReady) Status = "Ready (Local Build)";
            }
            else
            {
                DependenciesDir = Path.Combine(configDir, "Dependencies");
                CheckDependencies();
            }
        }

        private void CheckDependencies()
        {
            string cefPath = Path.Combine(DependenciesDir, "cef", "libcef.dll");
            string vlcPath = Path.Combine(DependenciesDir, "libvlc", "win-x64", "libvlc.dll");
            string ffmpegPath = Path.Combine(DependenciesDir, "ffmpeg.exe");

            if (File.Exists(cefPath) && File.Exists(vlcPath))
            {
                IsReady = true;
                Status = "Ready";
            }
            else
            {
                IsReady = false;
                Status = "Missing media dependencies. Click to download.";
            }

            if (!File.Exists(ffmpegPath) && !IsDownloading)
            {
                _pluginLog.Information("ffmpeg.exe not found! Auto-downloading...");
                _ = Task.Run(async () => await DownloadFFmpegAsync());
            }
        }

        public async Task DownloadDependenciesAsync()
        {
            if (IsDownloading || IsReady) return;

            IsDownloading = true;
            HasError = false;
            DownloadProgress = 0f;
            Status = "Starting download...";

            try
            {
                Directory.CreateDirectory(DependenciesDir);
                string zipPath = Path.Combine(DependenciesDir, "Dependencies.zip");

                // Download URL for the dependencies zip from the GitHub release
                string url = $"https://github.com/Sebane1/XivMediaPlayer/releases/download/{_version}/XivMediaPlayer-Dependencies.zip";
                
                _pluginLog.Information($"Downloading dependencies from: {url}");

                bool success = await TryDownloadDependencies(url, zipPath);
                
                if (!success) {
                    string fallbackUrl = "https://github.com/Sebane1/XivMediaPlayer/releases/latest/download/XivMediaPlayer-Dependencies.zip";
                    _pluginLog.Information($"Version {_version} not found. Falling back to latest release: {fallbackUrl}");
                    success = await TryDownloadDependencies(fallbackUrl, zipPath);
                }

                if (!success) {
                    _pluginLog.Information("Direct fallback failed. Attempting to resolve via GitHub API...");
                    string apiUrl = "https://api.github.com/repos/Sebane1/XivMediaPlayer/releases/latest";
                    string actualUrl = await ResolveLatestAssetUrl(apiUrl, "XivMediaPlayer-Dependencies.zip");
                    if (!string.IsNullOrEmpty(actualUrl)) {
                        _pluginLog.Information($"Resolved latest asset URL: {actualUrl}");
                        success = await TryDownloadDependencies(actualUrl, zipPath);
                    }
                }

                if (!success) {
                    throw new Exception("Failed to download dependencies. The server returned 404 Not Found for all available URLs. Please try downloading manually from the GitHub releases page.");
                }

                Status = "Extracting dependencies... (This may take a minute)";
                _pluginLog.Information("Extracting Dependencies.zip...");

                await Task.Run(() =>
                {
                    // Clean up old folders if they exist
                    string cefDir = Path.Combine(DependenciesDir, "cef");
                    string vlcDir = Path.Combine(DependenciesDir, "libvlc");
                    
                    try {
                        if (Directory.Exists(cefDir)) Directory.Delete(cefDir, true);
                        if (Directory.Exists(vlcDir)) Directory.Delete(vlcDir, true);

                        ZipFile.ExtractToDirectory(zipPath, DependenciesDir, true);
                    } catch (Exception e) when (e is UnauthorizedAccessException || e is IOException) {
                        // DLLs are locked because they are already loaded into the game process memory.
                        // (Usually happens if the user reloaded the plugin via /xlplugins)
                        // We can safely silently ignore this because it means the files are already successfully installed!
                        _pluginLog.Warning("Dependencies are currently locked by the process. Skipping extraction and assuming existing files are valid.");
                    }
                });

                _pluginLog.Information("Extraction complete.");
                
                // Cleanup zip
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                IsReady = true;
                Status = "Dependencies installed successfully!";
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = ex.Message;
                Status = "Download failed.";
                _pluginLog.Error(ex, "Failed to download media dependencies.");
            }
            finally
            {
                IsDownloading = false;
            }

            if (!File.Exists(Path.Combine(DependenciesDir, "ffmpeg.exe")))
            {
                await DownloadFFmpegAsync();
            }
        }

        private async Task<string> ResolveLatestAssetUrl(string apiUrl, string assetName)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("XivMediaPlayer-Plugin");
                    var json = await client.GetStringAsync(apiUrl);
                    
                    // Simple string search to avoid adding Newtonsoft.Json dependency if not already present in this file
                    string searchStr = $"\"name\":\"{assetName}\"";
                    int nameIdx = json.IndexOf(searchStr);
                    if (nameIdx > 0)
                    {
                        // Look for browser_download_url nearby
                        int urlIdx = json.IndexOf("\"browser_download_url\":\"", nameIdx);
                        if (urlIdx > 0)
                        {
                            urlIdx += 24; // length of key
                            int endIdx = json.IndexOf("\"", urlIdx);
                            if (endIdx > urlIdx)
                            {
                                return json.Substring(urlIdx, endIdx - urlIdx);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Warning(ex, "Failed to resolve latest asset URL from GitHub API.");
            }
            return string.Empty;
        }

        private async Task<bool> TryDownloadDependencies(string url, string zipPath)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("XivMediaPlayer-Plugin");
                    
                    using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!response.IsSuccessStatusCode) {
                            _pluginLog.Warning($"URL returned {(int)response.StatusCode} {response.ReasonPhrase}: {url}");
                            return false;
                        }

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        var canReportProgress = totalBytes != -1;

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                        {
                            var buffer = new byte[8192];
                            var totalRead = 0L;
                            var isMoreToRead = true;

                            do
                            {
                                var read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read == 0)
                                {
                                    isMoreToRead = false;
                                }
                                else
                                {
                                    await fileStream.WriteAsync(buffer, 0, read);
                                    totalRead += read;

                                    if (canReportProgress)
                                    {
                                        DownloadProgress = (float)totalRead / totalBytes;
                                        Status = $"Downloading: {(totalRead / 1024 / 1024)}MB / {(totalBytes / 1024 / 1024)}MB";
                                    }
                                    else
                                    {
                                        Status = $"Downloading: {(totalRead / 1024 / 1024)}MB";
                                    }
                                }
                            } while (isMoreToRead);
                        }
                    }
                }
                return true;
            } catch (HttpRequestException) {
                return false;
            }
        }

        private async Task DownloadFFmpegAsync()
        {
            try
            {
                Status = "Downloading FFmpeg...";
                _pluginLog.Information("Downloading FFmpeg...");
                string zipPath = Path.Combine(DependenciesDir, "ffmpeg.zip");
                string url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

                using (var client = new HttpClient())
                using (var response = await client.GetAsync(url))
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                {
                    await stream.CopyToAsync(fileStream);
                }

                Status = "Extracting FFmpeg...";
                await Task.Run(() =>
                {
                    using (var archive = ZipFile.OpenRead(zipPath))
                    {
                        var entry = System.Linq.Enumerable.FirstOrDefault(archive.Entries, e => e.FullName.EndsWith("bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                        {
                            entry.ExtractToFile(Path.Combine(DependenciesDir, "ffmpeg.exe"), true);
                        }
                        else
                        {
                            ZipFile.ExtractToDirectory(zipPath, DependenciesDir, true);
                        }
                    }
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                });

                CheckDependencies();
            }
            catch (Exception ex)
            {
                _pluginLog.Error(ex, "Failed to download FFmpeg.");
            }
        }
    }
}
