using System;
using System.Collections.Generic;
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
                return response.IsSuccessStatusCode;
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

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }
}
