using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MediaPlayerCore.Twitch;

/// <summary>
/// Sends the same playback telemetry the Twitch web player uses (usher + spade minute-watched)
/// while the main player consumes the stream via yt-dlp/VLC.
/// </summary>
public sealed class TwitchViewerSession : IDisposable
{
    private const string ClientId = "kimne78kx3ncx6brgo4mv6wki5h1ko";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string GqlUrl = "https://gql.twitch.tv/gql";

    private static readonly Regex SettingsJsPattern = new(
        @"src=""(https://[\w.]+/config/settings\.[0-9a-f]{32}\.js)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SpadeUrlPattern = new(
        @"""spade_?url"": ?""(https://[.\w\-/]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private bool _disposed;

    public Action<string>? LogInfo { get; set; }
    public Action<string, Exception>? LogWarning { get; set; }

    public TwitchViewerSession()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        });
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Client-Id", ClientId);
    }

    public static bool IsTwitchLiveChannelUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("/videos/", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("/clip/", StringComparison.OrdinalIgnoreCase)) return false;
        if (url.Contains("/directory/", StringComparison.OrdinalIgnoreCase)) return false;
        return TryParseChannelLogin(url) != null;
    }

    public static string? TryParseChannelLogin(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return null;
        if (!uri.Host.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) return null;

        string path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path)) return null;

        string login = path.Split('/')[0];
        if (string.IsNullOrEmpty(login)) return null;

        return login.ToLowerInvariant() switch
        {
            "videos" or "directory" or "settings" or "downloads" or "inventory" or "drops" => null,
            _ => login
        };
    }

    public Task StartAsync(string pageUrl, string? channelLoginHint = null, string? cookiesFilePath = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        string? channelLogin = TryParseChannelLogin(pageUrl) ?? channelLoginHint?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(channelLogin))
        {
            LogWarning?.Invoke("[TwitchPresence] Could not parse channel login from URL.", new InvalidOperationException(pageUrl));
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loopTask = Task.Run(() => RunAsync(channelLogin, pageUrl, cookiesFilePath, token), token);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_cts == null) return;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task RunAsync(string channelLogin, string pageUrl, string? cookiesFilePath, CancellationToken token)
    {
        try
        {
            ApplyCookies(cookiesFilePath);
            string? userId = TryGetUserIdFromCookies(cookiesFilePath);
            bool loggedIn = !string.IsNullOrEmpty(userId);

            JObject streamInfo = await GqlAsync(
                "VideoPlayerStreamInfoOverlayChannel",
                "198492e0857f6aedead9665c81c5a06d67b25b58034649687124083ff288597d",
                new { channel = channelLogin },
                token).ConfigureAwait(false);

            JToken? user = streamInfo["data"]?["user"];
            JToken? stream = user?["stream"];
            if (stream == null)
            {
                LogInfo?.Invoke("[TwitchPresence] Channel is offline; viewer telemetry not started.");
                return;
            }

            string channelId = user!["id"]?.ToString() ?? "";
            string broadcastId = stream["id"]?.ToString() ?? "";
            string gameName = user["broadcastSettings"]?["game"]?["name"]?.ToString() ?? "";
            string gameId = user["broadcastSettings"]?["game"]?["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(broadcastId))
            {
                LogWarning?.Invoke("[TwitchPresence] Stream metadata was incomplete.", new InvalidOperationException(channelLogin));
                return;
            }

            string spadeUrl = await ResolveSpadeUrlAsync(channelLogin, token).ConfigureAwait(false);
            LogInfo?.Invoke($"[TwitchPresence] Reporting live viewing for {channelLogin} (logged in: {loggedIn}).");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await SendPlaybackSignalsAsync(
                        channelLogin,
                        channelId,
                        broadcastId,
                        gameName,
                        gameId,
                        userId,
                        loggedIn,
                        spadeUrl,
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogWarning?.Invoke("[TwitchPresence] Heartbeat failed; will retry.", ex);
                }

                int delayMs = 55000 + Random.Shared.Next(0, 10000);
                await Task.Delay(delayMs, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            LogWarning?.Invoke("[TwitchPresence] Session ended with error.", ex);
        }
    }

    private async Task SendPlaybackSignalsAsync(
        string channelLogin,
        string channelId,
        string broadcastId,
        string gameName,
        string gameId,
        string? userId,
        bool loggedIn,
        string spadeUrl,
        CancellationToken token)
    {
        JObject tokenResponse = await GqlAsync(
            "PlaybackAccessToken",
            "ed230aa1e33e07eebb8928504583da78a5173989fadfb1ac94be06a04f3cdbe9",
            new
            {
                isLive = true,
                isVod = false,
                login = channelLogin,
                platform = "web",
                playerType = "site",
                vodID = ""
            },
            token).ConfigureAwait(false);

        JToken? playbackToken = tokenResponse["data"]?["streamPlaybackAccessToken"];
        string tokenValue = playbackToken?["value"]?.ToString() ?? "";
        string tokenSignature = playbackToken?["signature"]?.ToString() ?? "";
        if (string.IsNullOrEmpty(tokenValue) || string.IsNullOrEmpty(tokenSignature))
        {
            throw new InvalidOperationException("Playback access token missing.");
        }

        string usherUrl =
            $"https://usher.ttvnw.net/api/channel/hls/{channelLogin}.m3u8?sig={Uri.EscapeDataString(tokenSignature)}&token={Uri.EscapeDataString(tokenValue)}&player_type=site&allow_source=true";

        using var playlistRequest = new HttpRequestMessage(HttpMethod.Get, usherUrl);
        using HttpResponseMessage playlistResponse = await _http.SendAsync(playlistRequest, token).ConfigureAwait(false);
        string playlistBody = await playlistResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!playlistResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Usher playlist failed ({(int)playlistResponse.StatusCode}).");
        }

        if (TryParseTwitchError(playlistBody, out string usherError))
        {
            throw new InvalidOperationException($"Usher playlist error: {usherError}");
        }

        string? qualityPlaylistUrl = ExtractLastPlaylistUrl(playlistBody);
        if (!string.IsNullOrEmpty(qualityPlaylistUrl))
        {
            using var qualityRequest = new HttpRequestMessage(HttpMethod.Get, qualityPlaylistUrl);
            qualityRequest.Headers.ConnectionClose = true;
            using HttpResponseMessage qualityResponse = await _http.SendAsync(qualityRequest, token).ConfigureAwait(false);
            string qualityBody = await qualityResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (qualityResponse.IsSuccessStatusCode && !TryParseTwitchError(qualityBody, out _))
            {
                string? chunkUrl = ExtractChunkUrl(qualityBody);
                if (!string.IsNullOrEmpty(chunkUrl))
                {
                    using var headRequest = new HttpRequestMessage(HttpMethod.Head, chunkUrl);
                    headRequest.Headers.ConnectionClose = true;
                    using HttpResponseMessage headResponse = await _http.SendAsync(headRequest, token).ConfigureAwait(false);
                    if (!headResponse.IsSuccessStatusCode)
                    {
                        LogWarning?.Invoke($"[TwitchPresence] Chunk HEAD returned {(int)headResponse.StatusCode}.", new InvalidOperationException(chunkUrl));
                    }
                }
            }
        }

        var payload = new JArray
        {
            new JObject
            {
                ["event"] = "minute-watched",
                ["properties"] = new JObject
                {
                    ["broadcast_id"] = broadcastId,
                    ["channel_id"] = channelId,
                    ["channel"] = channelLogin,
                    ["client_time"] = DateTime.UtcNow.ToString("o"),
                    ["game"] = gameName,
                    ["game_id"] = gameId,
                    ["hidden"] = false,
                    ["is_live"] = true,
                    ["live"] = true,
                    ["logged_in"] = loggedIn,
                    ["minutes_logged"] = 1,
                    ["muted"] = false,
                    ["player"] = "site",
                    ["user_id"] = loggedIn ? userId : null
                }
            }
        };

        string json = payload.ToString(Formatting.None);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = encoded });
        using var spadeRequest = new HttpRequestMessage(HttpMethod.Post, spadeUrl) { Content = content };
        using HttpResponseMessage spadeResponse = await _http.SendAsync(spadeRequest, token).ConfigureAwait(false);
        if (spadeResponse.StatusCode != HttpStatusCode.NoContent && !spadeResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Spade POST failed ({(int)spadeResponse.StatusCode}).");
        }
    }

    private async Task<string> ResolveSpadeUrlAsync(string channelLogin, CancellationToken token)
    {
        string pageUrl = $"https://www.twitch.tv/{channelLogin}";
        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        using HttpResponseMessage pageResponse = await _http.SendAsync(pageRequest, token).ConfigureAwait(false);
        string html = await pageResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!pageResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Channel page failed ({(int)pageResponse.StatusCode}).");
        }

        Match match = SpadeUrlPattern.Match(html);
        if (!match.Success)
        {
            match = SettingsJsPattern.Match(html);
            if (!match.Success)
            {
                throw new InvalidOperationException("Could not locate Twitch settings JS on channel page.");
            }

            using var settingsRequest = new HttpRequestMessage(HttpMethod.Get, match.Groups[1].Value);
            using HttpResponseMessage settingsResponse = await _http.SendAsync(settingsRequest, token).ConfigureAwait(false);
            string settingsJs = await settingsResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!settingsResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Settings JS failed ({(int)settingsResponse.StatusCode}).");
            }

            match = SpadeUrlPattern.Match(settingsJs);
            if (!match.Success)
            {
                throw new InvalidOperationException("Could not locate spade_url in Twitch settings JS.");
            }
        }

        return match.Groups[1].Value;
    }

    private async Task<JObject> GqlAsync(string operationName, string sha256Hash, object variables, CancellationToken token)
    {
        var body = new JObject
        {
            ["operationName"] = operationName,
            ["extensions"] = new JObject
            {
                ["persistedQuery"] = new JObject
                {
                    ["version"] = 1,
                    ["sha256Hash"] = sha256Hash
                }
            },
            ["variables"] = JObject.FromObject(variables)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, GqlUrl)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
        string text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GQL {operationName} failed ({(int)response.StatusCode}): {text}");
        }

        JObject parsed = JObject.Parse(text);
        if (parsed["errors"] is JArray { Count: > 0 } errors)
        {
            throw new InvalidOperationException($"GQL {operationName} error: {errors[0]?["message"]}");
        }

        return parsed;
    }

    private void ApplyCookies(string? cookiesFilePath)
    {
        string? cookieHeader = BuildCookieHeader(cookiesFilePath);
        _http.DefaultRequestHeaders.Remove("Cookie");
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookieHeader);
        }
    }

    private static string? BuildCookieHeader(string? cookiesFilePath)
    {
        if (string.IsNullOrEmpty(cookiesFilePath) || !File.Exists(cookiesFilePath)) return null;

        var pairs = new List<string>();
        foreach (string line in File.ReadAllLines(cookiesFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 7) continue;

            string domain = parts[0];
            if (!domain.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) continue;

            string name = parts[5];
            string value = parts[6];
            if (string.IsNullOrEmpty(name)) continue;
            pairs.Add($"{name}={value}");
        }

        return pairs.Count == 0 ? null : string.Join("; ", pairs);
    }

    private static string? TryGetUserIdFromCookies(string? cookiesFilePath)
    {
        if (string.IsNullOrEmpty(cookiesFilePath) || !File.Exists(cookiesFilePath)) return null;

        foreach (string line in File.ReadAllLines(cookiesFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t');
            if (parts.Length < 7) continue;
            if (!parts[0].Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(parts[5], "auth-token", StringComparison.OrdinalIgnoreCase)) continue;
            return TryDecodeAuthTokenUserId(parts[6]);
        }

        return null;
    }

    private static string? TryDecodeAuthTokenUserId(string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken)) return null;
        try
        {
            string[] segments = authToken.Split('.');
            if (segments.Length < 2) return null;
            string payload = segments[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            payload = payload.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(payload);
            JObject json = JObject.Parse(Encoding.UTF8.GetString(bytes));
            return json["user_id"]?.ToString() ?? json["sub"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseTwitchError(string body, out string error)
    {
        error = "";
        if (!body.TrimStart().StartsWith('{')) return false;
        try
        {
            JObject json = JObject.Parse(body);
            error = json["error"]?.ToString() ?? json["message"]?.ToString() ?? "unknown";
            return !string.IsNullOrEmpty(error);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractLastPlaylistUrl(string masterPlaylist)
    {
        string? last = null;
        foreach (string rawLine in masterPlaylist.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                last = line;
            }
        }

        return last;
    }

    private static string? ExtractChunkUrl(string mediaPlaylist)
    {
        var lines = mediaPlaylist.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            string line = lines[i];
            if (line == "#EXT-X-ENDLIST" && i > 0)
            {
                line = lines[i - 1];
            }

            if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _http.Dispose();
    }
}
