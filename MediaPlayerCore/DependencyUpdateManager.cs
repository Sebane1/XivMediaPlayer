using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XivMediaPlayer
{
    public class DependencyUpdateManager : IDisposable
    {
        private readonly string _configDir;
        private readonly string _pluginDir;
        private readonly string _dependenciesDir;
        private readonly HttpClient _httpClient;

        public DependencyUpdateManager(string configDir, string pluginDir)
        {
            _configDir = configDir;
            _pluginDir = pluginDir;
            _dependenciesDir = Path.Combine(configDir, "Dependencies");
            _httpClient = new HttpClient();
        }

        public async Task<bool> CheckAndUpdateDependenciesAsync()
        {
            try
            {
                // For now we'll just check for version mismatches and log warnings if found
                string pluginVersion = this.GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                
                
                // Check dependencies directory structure - can be extended to actual update logic later
                bool updated = false;

                // Log current state of key components

                return updated;
            }
            catch (Exception ex)
            {
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
                // _pluginLog.Error($"Error fetching bgutil release: {ex.Message}");
                return "1.3.2";
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
                // _pluginLog.Error($"Error fetching bgutil release: {ex.Message}");
                return "1.3.2";
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