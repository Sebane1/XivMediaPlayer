using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace XivMediaPlayer.Networking
{
    public class DiscordAuthClient : IDisposable
    {
        private readonly ServerClient _serverClient;
        private readonly Configuration _config;
        private readonly IPluginLog _log;
        private readonly HttpClient _httpClient;
        private HttpListener? _localListener;
        private CancellationTokenSource? _authCts;

        public bool IsLoggedIn => !string.IsNullOrEmpty(_config.DiscordSessionToken);
        public string Username => _config.DiscordUsername;

        public DiscordAuthClient(ServerClient serverClient, Configuration config, IPluginLog log)
        {
            _serverClient = serverClient;
            _config = config;
            _log = log;
            _httpClient = new HttpClient();

            if (!string.IsNullOrEmpty(_config.DiscordSessionToken))
            {
                _serverClient.SetDiscordSessionToken(_config.DiscordSessionToken);
            }
        }

        public class LoginUrlResponse
        {
            public string url { get; set; } = string.Empty;
            public string state { get; set; } = string.Empty;
        }

        public class AuthUserInfo
        {
            public string token { get; set; } = string.Empty;
            public string discordId { get; set; } = string.Empty;
            public string username { get; set; } = string.Empty;
            public string avatarUrl { get; set; } = string.Empty;
        }

        public async Task<bool> StartLoginFlowAsync(Action<string> statusCallback)
        {
            try
            {
                string redirectUri = "http://localhost:59123/callback/";

                statusCallback("Requesting authentication URL...");
                string loginApi = $"{_serverClient.BaseUrl}/api/auth/discord/login?redirectUri={Uri.EscapeDataString(redirectUri)}";
                var resp = await _httpClient.GetAsync(loginApi);
                if (!resp.IsSuccessStatusCode)
                {
                    statusCallback("Server returned error when requesting login URL.");
                    return false;
                }

                var loginData = await resp.Content.ReadFromJsonAsync<LoginUrlResponse>();
                if (loginData == null || string.IsNullOrEmpty(loginData.url))
                {
                    statusCallback("Failed to parse login URL from server.");
                    return false;
                }

                _authCts?.Cancel();
                _authCts = new CancellationTokenSource();

                // Start local HTTP listener for OAuth completion callback
                StartLocalListener(redirectUri, statusCallback, _authCts.Token);

                // Open default browser to login URL
                statusCallback("Opening browser for Discord login...");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = loginData.url,
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start Discord OAuth flow.");
                statusCallback($"Error starting login: {ex.Message}");
                return false;
            }
        }

        private void StartLocalListener(string prefix, Action<string> statusCallback, CancellationToken token)
        {
            try
            {
                if (_localListener != null && _localListener.IsListening)
                {
                    try { _localListener.Stop(); } catch { }
                }

                _localListener = new HttpListener();
                _localListener.Prefixes.Add(prefix);
                _localListener.Start();

                Task.Run(async () =>
                {
                    try
                    {
                        while (!token.IsCancellationRequested && _localListener.IsListening)
                        {
                            var ctxTask = _localListener.GetContextAsync();
                            var completed = await Task.WhenAny(ctxTask, Task.Delay(120000, token));
                            if (completed != ctxTask)
                            {
                                statusCallback("Login timed out after 2 minutes.");
                                break;
                            }

                            var ctx = await ctxTask;
                            string reqCode = ctx.Request.QueryString["code"] ?? string.Empty;

                            if (!string.IsNullOrEmpty(reqCode))
                            {
                                statusCallback("Exchanging authorization code...");
                                try
                                {
                                    string cbUrl = $"{_serverClient.BaseUrl}/api/auth/discord/callback?code={Uri.EscapeDataString(reqCode)}&redirectUri={Uri.EscapeDataString(prefix)}&json=1";
                                    using var cbReq = new HttpRequestMessage(HttpMethod.Get, cbUrl);
                                    cbReq.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                                    var cbResp = await _httpClient.SendAsync(cbReq);
                                    string rawResp = await cbResp.Content.ReadAsStringAsync();

                                    byte[] buf = System.Text.Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif;background:#1e1e24;color:#fff;text-align:center;padding:40px;'><h2>Authentication Successful!</h2><p>You can close this tab and return to FFXIV.</p></body></html>");
                                    ctx.Response.ContentType = "text/html";
                                    ctx.Response.OutputStream.Write(buf, 0, buf.Length);
                                    ctx.Response.Close();

                                    if (cbResp.IsSuccessStatusCode)
                                    {
                                        AuthUserInfo? authResult = null;
                                        string trimmed = rawResp.TrimStart();
                                        if (trimmed.StartsWith("{"))
                                        {
                                            try
                                            {
                                                authResult = JsonSerializer.Deserialize<AuthUserInfo>(rawResp);
                                            }
                                            catch (Exception ex)
                                            {
                                                _log.Warning(ex, "Failed to parse JSON auth response, falling back to HTML extraction.");
                                            }
                                        }

                                        // Fallback: extract token, username, and discordId from HTML if server returned HTML
                                        if (authResult == null || string.IsNullOrEmpty(authResult.token))
                                        {
                                            var tokenMatch = System.Text.RegularExpressions.Regex.Match(rawResp, @"token:\s*['""]([^'""]+)['""]");
                                            var userMatch = System.Text.RegularExpressions.Regex.Match(rawResp, @"username:\s*['""]([^'""]+)['""]");
                                            var idMatch = System.Text.RegularExpressions.Regex.Match(rawResp, @"discordId:\s*['""]([^'""]+)['""]");

                                            if (tokenMatch.Success)
                                            {
                                                authResult = new AuthUserInfo
                                                {
                                                    token = tokenMatch.Groups[1].Value,
                                                    username = userMatch.Success ? userMatch.Groups[1].Value : "Discord User",
                                                    discordId = idMatch.Success ? idMatch.Groups[1].Value : ""
                                                };
                                            }
                                        }

                                        if (authResult != null && !string.IsNullOrEmpty(authResult.token))
                                        {
                                            _config.DiscordSessionToken = authResult.token;
                                            _config.DiscordUsername = authResult.username;
                                            _config.DiscordUserId = authResult.discordId;
                                            _serverClient.SetDiscordSessionToken(authResult.token);

                                            statusCallback($"Logged in as {authResult.username}");
                                            _log.Information($"Discord auth successful for user {authResult.username} ({authResult.discordId})");
                                        }
                                        else
                                        {
                                            _log.Error($"Server returned unparseable response: {rawResp}");
                                            statusCallback($"Failed to parse authentication response.");
                                        }
                                    }
                                    else
                                    {
                                        _log.Error($"Server returned error {cbResp.StatusCode}: {rawResp}");
                                        statusCallback($"Server error during code exchange ({cbResp.StatusCode}): {rawResp}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _log.Error(ex, "Failed to exchange authorization code.");
                                    statusCallback($"Code exchange error: {ex.Message}");
                                }
                                break;
                            }
                            else
                            {
                                ctx.Response.StatusCode = 400;
                                ctx.Response.Close();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                        {
                            _log.Error(ex, "Error in local OAuth listener loop.");
                        }
                    }
                    finally
                    {
                        try { _localListener?.Stop(); } catch { }
                    }
                }, token);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to start local HttpListener on port 59123.");
            }
        }

        public async Task<bool> ValidateAndSaveSessionAsync(string token, Action<string> statusCallback)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_serverClient.BaseUrl}/api/auth/me");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var resp = await _httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var userInfo = await resp.Content.ReadFromJsonAsync<AuthUserInfo>();
                    if (userInfo != null && !string.IsNullOrEmpty(userInfo.discordId))
                    {
                        _config.DiscordSessionToken = token;
                        _config.DiscordUsername = userInfo.username;
                        _config.DiscordUserId = userInfo.discordId;
                        _serverClient.SetDiscordSessionToken(token);

                        statusCallback($"Logged in as {userInfo.username}");
                        _log.Information($"Discord auth successful for user {userInfo.username} ({userInfo.discordId})");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to validate session token.");
            }

            statusCallback("Failed to validate login session token.");
            return false;
        }

        public async Task SyncPlayerHashAsync(string playerHash)
        {
            if (string.IsNullOrWhiteSpace(playerHash) || string.IsNullOrEmpty(_config.DiscordSessionToken)) return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverClient.BaseUrl}/api/auth/playerhash?hash={Uri.EscapeDataString(playerHash)}");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.DiscordSessionToken);
                await _httpClient.SendAsync(req);
            }
            catch { }
        }

        public async Task LogoutAsync()
        {
            try
            {
                if (!string.IsNullOrEmpty(_config.DiscordSessionToken))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverClient.BaseUrl}/api/auth/logout");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.DiscordSessionToken);
                    await _httpClient.SendAsync(req);
                }
            }
            catch { }

            _config.DiscordSessionToken = string.Empty;
            _config.DiscordUsername = string.Empty;
            _config.DiscordUserId = string.Empty;
            _serverClient.SetDiscordSessionToken(null);
        }

        public void Dispose()
        {
            _authCts?.Cancel();
            try { _localListener?.Stop(); } catch { }
            _httpClient.Dispose();
        }
    }
}
