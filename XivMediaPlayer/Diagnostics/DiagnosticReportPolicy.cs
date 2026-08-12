using System;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace XivMediaPlayer.Diagnostics
{
    public sealed class DiagnosticReportEligibility
    {
        public bool CanSend { get; init; }
        public string BlockReason { get; init; } = string.Empty;
        public string? LatestVersion { get; init; }
        public bool IsOfficialBuild { get; init; } = true;
    }

    internal sealed class DiagnosticReportPolicy
    {
        public const string OfficialRepoJsonUrl =
            "https://raw.githubusercontent.com/Sebane1/XivMediaPlayer/refs/heads/main/repo.json";

        private const string OfficialMainRepoSource = "OFFICIAL";
        private const string DevPluginSource = "DEVPLUGIN";

        private static readonly HttpClient HttpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

        private OfficialRepoManifest? _cachedManifest;
        private DateTime _cachedManifestUtc = DateTime.MinValue;

        public async Task<DiagnosticReportEligibility> EvaluateAsync(IDalamudPluginInterface pluginInterface)
        {
            if (!IsOfficialPluginInstall(pluginInterface, out string forkReason))
            {
                return new DiagnosticReportEligibility
                {
                    CanSend = false,
                    IsOfficialBuild = false,
                    BlockReason = forkReason,
                };
            }

            OfficialRepoManifest? official = await GetOfficialManifestAsync().ConfigureAwait(false);
            if (official == null)
            {
                return new DiagnosticReportEligibility
                {
                    CanSend = false,
                    BlockReason =
                        "Could not verify the latest plugin version from repo.json. Check your internet connection and try again.",
                };
            }

            Version currentVersion = pluginInterface.Manifest.AssemblyVersion;
            string requiredVersionText = pluginInterface.IsTesting
                ? official.TestingAssemblyVersion
                : official.AssemblyVersion;

            if (!TryParseVersion(requiredVersionText, out Version requiredVersion))
            {
                return new DiagnosticReportEligibility
                {
                    CanSend = false,
                    LatestVersion = requiredVersionText,
                    BlockReason = "Could not read the latest plugin version from the official repo.json.",
                };
            }

            if (currentVersion < requiredVersion)
            {
                return new DiagnosticReportEligibility
                {
                    CanSend = false,
                    LatestVersion = requiredVersionText,
                    BlockReason =
                        $"Error reports require plugin version {requiredVersionText} or newer. " +
                        "Update XivMediaPlayer in the Dalamud plugin installer, then try again.",
                };
            }

            return new DiagnosticReportEligibility
            {
                CanSend = true,
                LatestVersion = requiredVersionText,
                IsOfficialBuild = true,
            };
        }

        private static bool IsOfficialPluginInstall(IDalamudPluginInterface pluginInterface, out string reason)
        {
            var manifest = pluginInterface.Manifest;
            if (!string.Equals(manifest.InternalName, "XivMediaPlayer", StringComparison.Ordinal))
            {
                reason =
                    "Error reports are only accepted from the official XivMediaPlayer plugin installed through Dalamud.";
                return false;
            }

            string source = pluginInterface.SourceRepository ?? string.Empty;
            if (IsOfficialSourceRepository(source))
            {
                reason = string.Empty;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(manifest.RepoUrl)
                && manifest.RepoUrl.Contains("github.com/Sebane1/XivMediaPlayer", StringComparison.OrdinalIgnoreCase))
            {
                reason = string.Empty;
                return true;
            }

            reason =
                "Error reports are only accepted from the official XivMediaPlayer repo.json feed, not third-party or forked plugin lists.";
            return false;
        }

        private static bool IsOfficialSourceRepository(string sourceRepository)
        {
            if (string.IsNullOrWhiteSpace(sourceRepository))
            {
                return false;
            }

            if (string.Equals(sourceRepository, OfficialMainRepoSource, StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceRepository, DevPluginSource, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return sourceRepository.Contains("Sebane1/XivMediaPlayer", StringComparison.OrdinalIgnoreCase)
                && sourceRepository.Contains("repo.json", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<OfficialRepoManifest?> GetOfficialManifestAsync()
        {
            if (_cachedManifest != null && (DateTime.UtcNow - _cachedManifestUtc).TotalMinutes < 60)
            {
                return _cachedManifest;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, OfficialRepoJsonUrl);
                request.Headers.UserAgent.ParseAdd("XivMediaPlayer-Diagnostics/1.0");
                using var response = await HttpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JArray array = JArray.Parse(json);
                if (array.Count == 0)
                {
                    return null;
                }

                OfficialRepoManifest? manifest = array[0].ToObject<OfficialRepoManifest>();
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.InternalName))
                {
                    return null;
                }

                _cachedManifest = manifest;
                _cachedManifestUtc = DateTime.UtcNow;
                return manifest;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseVersion(string? text, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim().TrimStart('v', 'V');
            return Version.TryParse(text, out version);
        }

        private sealed class OfficialRepoManifest
        {
            [JsonProperty("InternalName")]
            public string InternalName { get; set; } = string.Empty;

            [JsonProperty("RepoUrl")]
            public string RepoUrl { get; set; } = string.Empty;

            [JsonProperty("AssemblyVersion")]
            public string AssemblyVersion { get; set; } = string.Empty;

            [JsonProperty("TestingAssemblyVersion")]
            public string TestingAssemblyVersion { get; set; } = string.Empty;
        }
    }
}
