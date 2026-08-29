using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using XivMediaPlayer.Networking.Models;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Networking
{
    public class ServerClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        public string BaseUrl => _baseUrl;
        private readonly IPluginLog _log;

        public ServerClient(string baseUrl, IPluginLog log)
        {
            _baseUrl = baseUrl;
            _log = log;
            _httpClient = new HttpClient();
        }

        public void SetDiscordSessionToken(string? token)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Authorization = null;
            }
        }

        public async Task<long> GetServerTimeAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/time");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<long>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to fetch server time");
            }
            return 0;
        }

        public async Task<List<TvPlacement>> GetTvsForRoomAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs");
                if (response.IsSuccessStatusCode)
                {
                    var tvs = await response.Content.ReadFromJsonAsync<List<TvPlacement>>();
                    return tvs ?? new List<TvPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get TVs for room {locationKey}");
            }
            return new List<TvPlacement>();
        }

        public async Task<TvPlacement> RegisterTvAsync(string locationKey, TvPlacement placement, bool create = false)
        {
            return await RegisterTvInternalAsync(locationKey, placement, create, allowAutoCreate: true);
        }

        private async Task<TvPlacement> RegisterTvInternalAsync(
            string locationKey,
            TvPlacement placement,
            bool create,
            bool allowAutoCreate)
        {
            try
            {
                string url = $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs";
                if (create)
                {
                    url += "?create=true";
                }

                var response = await _httpClient.PostAsJsonAsync(url, placement);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("This TV is locked by its owner and cannot be moved.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<TvPlacement>();
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                if (allowAutoCreate
                    && !create
                    && response.StatusCode == System.Net.HttpStatusCode.BadRequest
                    && errorBody.Contains("create=true", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(placement.Id))
                {
                    var serverTvs = await GetTvsForRoomAsync(locationKey);
                    if (!serverTvs.Any(t => string.Equals(t.Id, placement.Id, StringComparison.Ordinal)))
                    {
                        _log.Info($"TV id {placement.Id} is not on server for {locationKey}; registering as a new screen.");
                        return await RegisterTvInternalAsync(locationKey, placement, create: true, allowAutoCreate: false);
                    }
                }

                _log.Error($"Failed to register TV for room {locationKey} ({(int)response.StatusCode}): {errorBody}");
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to register TV for room {locationKey}");
                throw;
            }
            return null;
        }

        public async Task<bool> DeleteTvAsync(string locationKey, string tvId, string ownerId, bool bypassLock)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/tvs/{Uri.EscapeDataString(tvId)}?ownerId={Uri.EscapeDataString(ownerId)}&bypassLock={bypassLock}");
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("Cannot delete TV: It is locked by its owner.");
                }
                return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound;
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to delete TV for room {locationKey}");
                throw;
            }
        }

        public async Task<RoomMediaStateSync> GetMediaStateAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/media");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomMediaStateSync>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get media state for room {locationKey}");
            }
            return null;
        }

        public async Task<RoomMediaStateSync> UpdateMediaStateAsync(string locationKey, RoomMediaStateSync state)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/media", state);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("The TV in this room is locked by its owner.");
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    throw new InvalidOperationException("You are no longer the media owner.");
                }

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    var errorMsg = await response.Content.ReadAsStringAsync();
                    throw new ArgumentException(errorMsg);
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomMediaStateSync>();
                }
            }
            catch (InvalidOperationException) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to update media state for room {locationKey}");
                throw; // Rethrow so the plugin can catch and handle it
            }
            return null;
        }

        public async Task<List<TvPlacement>> GetTvsBatchAsync(List<string> locationKeys)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/batch/tvs", locationKeys);
                if (response.IsSuccessStatusCode)
                {
                    var tvs = await response.Content.ReadFromJsonAsync<List<TvPlacement>>();
                    return tvs ?? new List<TvPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to get TVs in batch");
            }
            return new List<TvPlacement>();
        }

        public async Task<List<RoomMediaStateSync>> GetMediaStatesBatchAsync(List<string> locationKeys)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/rooms/batch/media", locationKeys);
                if (response.IsSuccessStatusCode)
                {
                    var states = await response.Content.ReadFromJsonAsync<List<RoomMediaStateSync>>();
                    return states ?? new List<RoomMediaStateSync>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to get media states in batch");
            }
            return new List<RoomMediaStateSync>();
        }

        public async Task<RoomVenueSettings?> GetVenueSettingsAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/venue");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomVenueSettings>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get venue settings for room {locationKey}");
            }

            return null;
        }

        public async Task<RoomVenueSettings?> UpdateVenueSettingsAsync(string locationKey, RoomVenueSettings settings)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(
                    $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/venue", settings);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("The TV in this room is locked by its owner.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<RoomVenueSettings>();
                }
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to update venue settings for room {locationKey}");
                throw;
            }

            return null;
        }

        public async Task<List<BannerPlacement>> GetBannersForRoomAsync(string locationKey)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/banners");
                if (response.IsSuccessStatusCode)
                {
                    var banners = await response.Content.ReadFromJsonAsync<List<BannerPlacement>>();
                    return banners ?? new List<BannerPlacement>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to get banners for room {locationKey}");
            }

            return new List<BannerPlacement>();
        }

        public async Task<BannerPlacement?> RegisterBannerAsync(string locationKey, BannerPlacement placement, bool create = false)
        {
            try
            {
                string url = $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/banners";
                if (create)
                {
                    url += "?create=true";
                }

                var response = await _httpClient.PostAsJsonAsync(url, placement);
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("This room is locked by its owner.");
                }

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<BannerPlacement>();
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                _log.Error($"Failed to register banner for room {locationKey} ({(int)response.StatusCode}): {errorBody}");
            }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to register banner for room {locationKey}");
                throw;
            }

            return null;
        }

        public async Task<bool> DeleteBannerAsync(string locationKey, string bannerId, string ownerId, bool bypassLock)
        {
            try
            {
                var response = await _httpClient.DeleteAsync(
                    $"{_baseUrl}/api/rooms/{Uri.EscapeDataString(locationKey)}/banners/{Uri.EscapeDataString(bannerId)}?ownerId={Uri.EscapeDataString(ownerId)}&bypassLock={bypassLock}");
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    throw new UnauthorizedAccessException("Cannot delete banner: It is locked by its owner.");
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to delete banner for room {locationKey}");
                throw;
            }
        }

        public async Task<DiagnosticLogSubmitResult?> SubmitDiagnosticLogsAsync(DiagnosticLogReport report)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/diagnostics/logs")
                {
                    Content = JsonContent.Create(report),
                };
                request.Headers.UserAgent.ParseAdd("XivMediaPlayer/1.0");

                using var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    DiagnosticLogSubmitResult? result = await response.Content.ReadFromJsonAsync<DiagnosticLogSubmitResult>();
                    if (result != null)
                    {
                        result.Success = true;
                    }

                    return result;
                }

                string body = await response.Content.ReadAsStringAsync();
                _log.Warning($"Diagnostic log upload failed ({(int)response.StatusCode}): {body}");

                return new DiagnosticLogSubmitResult
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(body)
                        ? "The server rejected the error report."
                        : body,
                };
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to upload diagnostic logs");
            }

            return new DiagnosticLogSubmitResult
            {
                Success = false,
                ErrorMessage = "Could not send error report. Check your internet connection and try again.",
            };
        }

        public async Task<List<WatchPartyEvent>> GetEventsAsync(string? datacenter = null, string? world = null)
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                string url = $"{cleanBase}/api/events";
                var queryParams = new List<string>();
                if (!string.IsNullOrWhiteSpace(datacenter)) queryParams.Add($"datacenter={Uri.EscapeDataString(datacenter)}");
                if (!string.IsNullOrWhiteSpace(world)) queryParams.Add($"world={Uri.EscapeDataString(world)}");

                if (queryParams.Count > 0) url += "?" + string.Join("&", queryParams);

                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var events = await response.Content.ReadFromJsonAsync<List<WatchPartyEvent>>();
                    return events ?? new List<WatchPartyEvent>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to fetch watch party events");
            }
            return new List<WatchPartyEvent>();
        }

        public async Task<WatchPartyEvent?> CreateEventAsync(WatchPartyEvent watchEvent)
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                var response = await _httpClient.PostAsJsonAsync($"{cleanBase}/api/events", watchEvent);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<WatchPartyEvent>();
                }
                string err = await response.Content.ReadAsStringAsync();
                _log.Warning($"Create event failed: {response.StatusCode} - {err}");
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to create watch party event");
            }
            return null;
        }

        public async Task<bool> DeleteEventAsync(string eventId)
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                var response = await _httpClient.DeleteAsync($"{cleanBase}/api/events/{Uri.EscapeDataString(eventId)}");
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"Failed to delete watch party event {eventId}");
            }
            return false;
        }

        public class BotApiKeyDto
        {
            public string KeyHashPrefix { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public DateTime CreatedAtUtc { get; set; }
            public DateTime? LastUsedUtc { get; set; }
        }

        public class GenerateBotKeyResult
        {
            public string ApiKey { get; set; } = string.Empty;
            public string Label { get; set; } = string.Empty;
            public string DiscordId { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
        }

        public async Task<GenerateBotKeyResult?> GenerateBotApiKeyAsync(string label)
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                var response = await _httpClient.PostAsJsonAsync($"{cleanBase}/api/auth/bot-key/generate", new { label });
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<GenerateBotKeyResult>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to generate Bot API Key");
            }
            return null;
        }

        public async Task<List<BotApiKeyDto>> ListBotApiKeysAsync()
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                var response = await _httpClient.GetAsync($"{cleanBase}/api/auth/bot-key/list");
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<List<BotApiKeyDto>>() ?? new List<BotApiKeyDto>();
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to list Bot API Keys");
            }
            return new List<BotApiKeyDto>();
        }

        public async Task<bool> RevokeBotApiKeyAsync(string keyHashPrefix)
        {
            try
            {
                string cleanBase = _baseUrl.TrimEnd('/');
                var response = await _httpClient.PostAsync($"{cleanBase}/api/auth/bot-key/revoke?keyHashPrefix={Uri.EscapeDataString(keyHashPrefix)}", null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to revoke Bot API Key");
            }
            return false;
        }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
