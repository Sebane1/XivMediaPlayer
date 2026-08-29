using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer
{
    public class DependencyUpdateManager
    {
        private readonly string _configDir;
        private readonly string _pluginDir;
        private readonly IPluginLog _pluginLog;
        private readonly string _dependenciesDir;
        private readonly HttpClient _httpClient;

        public DependencyUpdateManager(string configDir, string pluginDir, IPluginLog pluginLog)
        {
            _configDir = configDir;
            _pluginDir = pluginDir;
            _pluginLog = pluginLog;
            _dependenciesDir = Path.Combine(configDir, "Dependencies");
            _httpClient = new HttpClient();
        }

        public async Task<bool> CheckAndUpdateDependenciesAsync()
        {
            try
            {
                // For now we'll just check for version mismatches and log warnings if found
                string pluginVersion = this.GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                
                _pluginLog.Information($"Checking for dependency updates...");
                
                // Check dependencies directory structure - can be extended to actual update logic later
                bool updated = false;

                // Log current state of key components
                if (Directory.Exists(_dependenciesDir))
                {
                    var files = Directory.GetFiles(_dependenciesDir, "*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        _pluginLog.Information($"Dependency file: {file}");
                    }
                }

                return updated;
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error checking for dependency updates: {ex.Message}");
                return false;
            }
        }

        public async Task<string> GetLatestBgutilReleaseVersionAsync()
        {
            try
            {
                // GitHub API endpoint for bgutil-ytdlp-pot-provider releases
                string apiUrl = "https://api.github.com/repos/Brainicism/bgutil-ytdlp-pot-provider/releases";
                
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "XivMediaPlayer");
                
                var response = await _httpClient.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    
                    // Extract latest version using regex
                    var match = Regex.Match(content, @"""tag_name"":\s*""v?(\d+\.\d+\.\d+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error fetching bgutil release: {ex.Message}");
            }

            // Return default version if unable to fetch
            return "1.3.2";
        }

        public async Task<string> GetLatestBgutilReleaseVersionAsync(string repoOwner, string repoName)
        {
            try
            {
                string apiUrl = $"https://api.github.com/repos/{repoOwner}/{repoName}/releases/latest";
                
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "XivMediaPlayer");
                
                var response = await _httpClient.GetAsync(apiUrl);
                if (response.IsSuccessStatusCode)
                {
                    string content = await response.Content.ReadAsStringAsync();
                    
                    // Extract latest version using regex
                    var match = Regex.Match(content, @"""tag_name"":\s*""v?(\d+\.\d+\.\d+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                _pluginLog.Error($"Error fetching bgutil release: {ex.Message}");
            }

            // Return default version if unable to fetch
            return "1.3.2";
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}