using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivMediaPlayer.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<AuthController> _logger;
        private readonly IConfiguration _config;
        private static readonly HttpClient _httpClient = new HttpClient();

        public AuthController(AppDbContext db, ILogger<AuthController> logger, IConfiguration config)
        {
            _db = db;
            _logger = logger;
            _config = config;
        }

        public class DiscordTokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("token_type")]
            public string TokenType { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = string.Empty;

            [JsonPropertyName("scope")]
            public string Scope { get; set; } = string.Empty;
        }

        public class DiscordUserProfile
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = string.Empty;

            [JsonPropertyName("username")]
            public string Username { get; set; } = string.Empty;

            [JsonPropertyName("global_name")]
            public string? GlobalName { get; set; }

            [JsonPropertyName("avatar")]
            public string? Avatar { get; set; }
        }

        [HttpGet("discord/login")]
        public IActionResult GetDiscordLoginUrl([FromQuery] string? redirectUri = null)
        {
            string clientId = _config["Discord:ClientId"] ?? string.Empty;
            if (string.IsNullOrEmpty(clientId))
            {
                return BadRequest("Discord Client ID is not configured on the server.");
            }

            string configuredRedirect = _config["Discord:RedirectUri"] ?? $"{Request.Scheme}://{Request.Host}/api/auth/discord/callback";
            string targetRedirect = redirectUri ?? configuredRedirect;
            string state = Guid.NewGuid().ToString("N");

            string loginUrl = $"https://discord.com/api/oauth2/authorize?client_id={clientId}" +
                             $"&redirect_uri={Uri.EscapeDataString(targetRedirect)}" +
                             $"&response_type=code&scope=identify&state={state}";

            return Ok(new { url = loginUrl, state });
        }

        [HttpGet("discord/callback")]
        public async Task<IActionResult> DiscordCallback([FromQuery] string code, [FromQuery] string? redirectUri = null)
        {
            if (string.IsNullOrEmpty(code))
            {
                return BadRequest("Missing OAuth authorization code.");
            }

            string clientId = _config["Discord:ClientId"] ?? string.Empty;
            string clientSecret = _config["Discord:ClientSecret"] ?? string.Empty;
            string targetRedirect = redirectUri ?? _config["Discord:RedirectUri"] ?? $"{Request.Scheme}://{Request.Host}/api/auth/discord/callback";

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                return BadRequest("Discord Client configuration missing on server.");
            }

            try
            {
                // 1. Exchange code for access token
                var tokenReqParams = new Dictionary<string, string>
                {
                    { "client_id", clientId },
                    { "client_secret", clientSecret },
                    { "grant_type", "authorization_code" },
                    { "code", code },
                    { "redirect_uri", targetRedirect }
                };

                using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token")
                {
                    Content = new FormUrlEncodedContent(tokenReqParams)
                };

                var tokenResp = await _httpClient.SendAsync(tokenReq);
                if (!tokenResp.IsSuccessStatusCode)
                {
                    string errContent = await tokenResp.Content.ReadAsStringAsync();
                    _logger.LogError("Discord token exchange failed: {Error}", errContent);
                    return BadRequest("Failed to exchange Discord authorization code.");
                }

                var tokenData = JsonSerializer.Deserialize<DiscordTokenResponse>(await tokenResp.Content.ReadAsStringAsync());
                if (tokenData == null || string.IsNullOrEmpty(tokenData.AccessToken))
                {
                    return BadRequest("Invalid token response from Discord.");
                }

                // 2. Fetch user profile
                using var userReq = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/users/@me");
                userReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokenData.AccessToken);

                var userResp = await _httpClient.SendAsync(userReq);
                if (!userResp.IsSuccessStatusCode)
                {
                    return BadRequest("Failed to fetch Discord user profile.");
                }

                var profile = JsonSerializer.Deserialize<DiscordUserProfile>(await userResp.Content.ReadAsStringAsync());
                if (profile == null || string.IsNullOrEmpty(profile.Id))
                {
                    return BadRequest("Invalid profile returned from Discord.");
                }

                string displayName = profile.GlobalName ?? profile.Username;
                string avatarUrl = !string.IsNullOrEmpty(profile.Avatar)
                    ? $"https://cdn.discordapp.com/avatars/{profile.Id}/{profile.Avatar}.png"
                    : string.Empty;

                // 3. Upsert DiscordUser
                var existingUser = await _db.DiscordUsers.FirstOrDefaultAsync(u => u.DiscordId == profile.Id);
                if (existingUser == null)
                {
                    existingUser = new DiscordUser
                    {
                        DiscordId = profile.Id,
                        Username = displayName,
                        AvatarUrl = avatarUrl,
                        CreatedAtUtc = DateTime.UtcNow,
                        LastSeenUtc = DateTime.UtcNow
                    };
                    _db.DiscordUsers.Add(existingUser);
                }
                else
                {
                    existingUser.Username = displayName;
                    existingUser.AvatarUrl = avatarUrl;
                    existingUser.LastSeenUtc = DateTime.UtcNow;
                    _db.DiscordUsers.Update(existingUser);
                }

                // 4. Create UserSession (store SHA-256 hash in DB, send raw token to client)
                string rawToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                string hashedToken = HashToken(rawToken);

                var session = new UserSession
                {
                    Token = hashedToken,
                    DiscordId = profile.Id,
                    CreatedAtUtc = DateTime.UtcNow,
                    LastUsedUtc = DateTime.UtcNow,
                    ExpiresUtc = DateTime.UtcNow.AddDays(60)
                };

                _db.UserSessions.Add(session);
                await _db.SaveChangesAsync();

                // Return JSON for programmatic (plugin) callers, HTML for browser callers
                string acceptHeader = Request.Headers["Accept"].ToString();
                bool wantsJson = acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) 
                              || Request.Query.ContainsKey("json");

                if (wantsJson)
                {
                    return Ok(new
                    {
                        token = rawToken,
                        discordId = profile.Id,
                        username = displayName,
                        avatarUrl = avatarUrl
                    });
                }

                // Browser callback: display success HTML
                string html = $@"
<!DOCTYPE html>
<html>
<head><title>XivMediaPlayer Auth Success</title></head>
<body style=""font-family:sans-serif; text-align:center; padding:40px; background:#1e1e24; color:#fff;"">
<h2>Successfully Logged in with Discord!</h2>
<p>Welcome, <strong>{HtmlEncode(displayName)}</strong>.</p>
<p>You may close this tab and return to Final Fantasy XIV.</p>
<script>
    if (window.opener) {{
        window.opener.postMessage({{ type: 'xivmp_auth_success', token: '{rawToken}', discordId: '{profile.Id}', username: '{HtmlEncode(displayName)}' }}, '*');
    }}
</script>
</body>
</html>";
                return Content(html, "text/html");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Discord OAuth callback.");
                return StatusCode(500, "Internal error processing authentication.");
            }
        }

        [HttpGet("me")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var session = await GetValidSessionAsync();
            if (session == null)
            {
                return Unauthorized(new { message = "Invalid or expired session token." });
            }

            var user = await _db.DiscordUsers.FirstOrDefaultAsync(u => u.DiscordId == session.DiscordId);
            if (user == null)
            {
                return NotFound();
            }

            return Ok(new
            {
                discordId = user.DiscordId,
                username = user.Username,
                playerHashes = user.GetPlayerHashes(),
                avatarUrl = user.AvatarUrl,
                lastSeenUtc = user.LastSeenUtc
            });
        }

        [HttpPost("playerhash")]
        public async Task<IActionResult> BindPlayerHash([FromQuery] string hash)
        {
            var session = await GetValidSessionAsync();
            if (session == null) return Unauthorized();

            if (string.IsNullOrWhiteSpace(hash)) return BadRequest();

            var cleanHash = hash.Trim().ToLowerInvariant();

            var user = await _db.DiscordUsers.FirstOrDefaultAsync(u => u.DiscordId == session.DiscordId);
            if (user != null)
            {
                var hashes = user.GetPlayerHashes();
                if (!hashes.Contains(cleanHash, StringComparer.OrdinalIgnoreCase))
                {
                    hashes.Add(cleanHash);
                    user.PlayerHashesJson = System.Text.Json.JsonSerializer.Serialize(hashes);
                }
                user.LastSeenUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return Ok(new { discordId = user.DiscordId, playerHashes = hashes });
            }

            return NotFound();
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            var session = await GetValidSessionAsync();
            if (session != null)
            {
                _db.UserSessions.Remove(session);
                await _db.SaveChangesAsync();
            }
            return Ok(new { message = "Logged out successfully." });
        }

        private async Task<UserSession?> GetValidSessionAsync()
        {
            string? token = null;
            if (Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                string raw = authHeader.ToString();
                if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    token = raw.Substring(7).Trim();
                }
            }

            if (string.IsNullOrEmpty(token) && Request.Query.TryGetValue("token", out var queryToken))
            {
                token = queryToken.ToString();
            }

            if (string.IsNullOrEmpty(token)) return null;

            string hashedToken = HashToken(token);
            var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == hashedToken);
            if (session == null || session.ExpiresUtc <= DateTime.UtcNow)
            {
                return null;
            }

            session.LastUsedUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return session;
        }

        private static string HashToken(string token)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string HtmlEncode(string val) => System.Net.WebUtility.HtmlEncode(val);
    }
}
