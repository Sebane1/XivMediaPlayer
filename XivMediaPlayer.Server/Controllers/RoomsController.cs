using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text;

namespace XivMediaPlayer.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class RoomsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<RoomsController> _logger;
        private readonly IConfiguration _config;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastFetchTimes = new();

        public RoomsController(AppDbContext db, ILogger<RoomsController> logger, IConfiguration config)
        {
            _db = db;
            _logger = logger;
            _config = config;
        }

        [HttpGet("{locationKey}/tvs")]
        public async Task<IActionResult> GetTvs(string locationKey)
        {
            var tvs = await _db.TvPlacements
                .Where(t => t.LocationKey == locationKey)
                .ToListAsync();
                
            return Ok(tvs);
        }

        [HttpGet("time")]
        public IActionResult GetServerTime()
        {
            return Ok(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        [HttpPost("{locationKey}/tvs")]
        public async Task<IActionResult> RegisterTv(string locationKey, [FromBody] TvPlacement placement, [FromQuery] bool create = false)
        {
            placement.LocationKey = locationKey;
            placement.LastUpdated = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(placement.Id))
            {
                placement.Id = Guid.NewGuid().ToString();
            }

            var roomTvs = await _db.TvPlacements
                .Where(t => t.LocationKey == locationKey)
                .ToListAsync();

            TvPlacement? existing = null;

            if (!create)
            {
                existing = roomTvs.FirstOrDefault(t => t.Id == placement.Id);

                // Legacy clients send a fresh random Id on every save but expect upsert-by-room.
                if (existing == null && roomTvs.Count == 1)
                {
                    existing = roomTvs[0];
                }
            }

            if (existing != null)
            {
                bool isForfeited = false;
                if (locationKey.StartsWith("zone_"))
                {
                    var lastFetch = _lastFetchTimes.TryGetValue(locationKey, out var lf) ? lf : DateTime.MinValue;
                    if ((DateTime.UtcNow - lastFetch).TotalMinutes >= 2)
                    {
                        isForfeited = true;
                    }
                }

                if (!isForfeited && existing.IsLocked && existing.OwnerId != placement.OwnerId && !placement.BypassLock)
                {
                    return Forbid();
                }

                if (isForfeited) existing.IsLocked = false; // Reset lock if it was abandoned

                ApplyPlacementFields(existing, placement);
                existing.LastUpdated = placement.LastUpdated;
                _db.TvPlacements.Update(existing);
                await _db.SaveChangesAsync();
                return Ok(existing);
            }

            if (create || roomTvs.Count == 0)
            {
                if (roomTvs.Count > 0 && !create)
                {
                    // Ambiguous legacy request against a multi-TV room — require explicit create=true.
                    return BadRequest("Multiple TVs exist for this location. Pass create=true to add another screen.");
                }

                _db.TvPlacements.Add(placement);
                await _db.SaveChangesAsync();
                return Ok(placement);
            }

            return BadRequest("Multiple TVs exist for this location. Pass create=true to add another screen.");
        }

        private static void ApplyPlacementFields(TvPlacement target, TvPlacement source)
        {
            target.PositionX = source.PositionX;
            target.PositionY = source.PositionY;
            target.PositionZ = source.PositionZ;
            target.RotationX = source.RotationX;
            target.RotationY = source.RotationY;
            target.RotationZ = source.RotationZ;
            target.ScaleX = source.ScaleX;
            target.ScaleY = source.ScaleY;
            target.Opacity = source.Opacity;
            target.IsProjectorMode = source.IsProjectorMode;
            target.ScreensaverColorR = source.ScreensaverColorR;
            target.ScreensaverColorG = source.ScreensaverColorG;
            target.ScreensaverColorB = source.ScreensaverColorB;
            target.ScreensaverStyle = source.ScreensaverStyle;
            target.IsLocked = source.IsLocked;
            target.OwnerId = source.OwnerId;
        }

        private async Task<bool> IsRoomMediaLockedAsync(string locationKey, string ownerId, bool bypassLock)
        {
            if (bypassLock) return false;

            return await _db.TvPlacements.AnyAsync(
                t => t.LocationKey == locationKey && t.IsLocked && t.OwnerId != ownerId);
        }

        [HttpDelete("{locationKey}/tvs/{tvId}")]
        public async Task<IActionResult> RemoveTv(string locationKey, string tvId, [FromQuery] string ownerId, [FromQuery] bool bypassLock = false)
        {
            var tv = await _db.TvPlacements.FirstOrDefaultAsync(t => t.LocationKey == locationKey && t.Id == tvId);
            if (tv != null)
            {
                if (tv.OwnerId != ownerId && !bypassLock)
                {
                    return StatusCode(403);
                }
                _db.TvPlacements.Remove(tv);
                await _db.SaveChangesAsync();
                return Ok();
            }
            return NotFound();
        }

        [HttpGet("{locationKey}/venue")]
        public async Task<IActionResult> GetVenueSettings(string locationKey)
        {
            var settings = await _db.RoomVenueSettings.FindAsync(locationKey);
            if (settings == null)
            {
                return Ok(new RoomVenueSettings { LocationKey = locationKey });
            }

            return Ok(settings);
        }

        [HttpPost("{locationKey}/venue")]
        public async Task<IActionResult> UpdateVenueSettings(string locationKey, [FromBody] RoomVenueSettings settings)
        {
            settings.LocationKey = locationKey;
            settings.LastUpdated = DateTime.UtcNow;

            if (await IsRoomMediaLockedAsync(locationKey, settings.OwnerId, settings.BypassLock))
            {
                return StatusCode(403);
            }

            var existing = await _db.RoomVenueSettings.FindAsync(locationKey);
            if (existing == null)
            {
                _db.RoomVenueSettings.Add(settings);
            }
            else
            {
                existing.IdleBrandingUrl = settings.IdleBrandingUrl ?? string.Empty;
                existing.OwnerId = settings.OwnerId;
                existing.LastUpdated = settings.LastUpdated;
                _db.RoomVenueSettings.Update(existing);
                settings = existing;
            }

            await _db.SaveChangesAsync();
            return Ok(settings);
        }

        [HttpGet("{locationKey}/banners")]
        public async Task<IActionResult> GetBanners(string locationKey)
        {
            var banners = await _db.BannerPlacements
                .Where(b => b.LocationKey == locationKey)
                .ToListAsync();

            return Ok(banners);
        }

        [HttpPost("{locationKey}/banners")]
        public async Task<IActionResult> RegisterBanner(string locationKey, [FromBody] BannerPlacement placement, [FromQuery] bool create = false)
        {
            placement.LocationKey = locationKey;
            placement.LastUpdated = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(placement.Id))
            {
                placement.Id = Guid.NewGuid().ToString();
            }

            if (await IsRoomMediaLockedAsync(locationKey, placement.OwnerId, placement.BypassLock))
            {
                return StatusCode(403);
            }

            var existing = await _db.BannerPlacements.FirstOrDefaultAsync(
                b => b.LocationKey == locationKey && b.Id == placement.Id);

            if (existing != null && !create)
            {
                if (existing.OwnerId != placement.OwnerId && !placement.BypassLock)
                {
                    return Forbid();
                }

                ApplyBannerFields(existing, placement);
                existing.LastUpdated = placement.LastUpdated;
                _db.BannerPlacements.Update(existing);
                await _db.SaveChangesAsync();
                return Ok(existing);
            }

            _db.BannerPlacements.Add(placement);
            await _db.SaveChangesAsync();
            return Ok(placement);
        }

        [HttpDelete("{locationKey}/banners/{bannerId}")]
        public async Task<IActionResult> RemoveBanner(string locationKey, string bannerId, [FromQuery] string ownerId, [FromQuery] bool bypassLock = false)
        {
            var banner = await _db.BannerPlacements.FirstOrDefaultAsync(
                b => b.LocationKey == locationKey && b.Id == bannerId);
            if (banner == null) return NotFound();

            if (banner.OwnerId != ownerId && !bypassLock)
            {
                return StatusCode(403);
            }

            _db.BannerPlacements.Remove(banner);
            await _db.SaveChangesAsync();
            return Ok();
        }

        private static void ApplyBannerFields(BannerPlacement target, BannerPlacement source)
        {
            target.PositionX = source.PositionX;
            target.PositionY = source.PositionY;
            target.PositionZ = source.PositionZ;
            target.RotationX = source.RotationX;
            target.RotationY = source.RotationY;
            target.RotationZ = source.RotationZ;
            target.ScaleX = source.ScaleX;
            target.ScaleY = source.ScaleY;
            target.ImageUrl = source.ImageUrl;
            target.Opacity = source.Opacity;
            target.OwnerId = source.OwnerId;
        }

        [HttpGet("{locationKey}/media")]
        public async Task<IActionResult> GetMediaState(string locationKey)
        {
            var state = await _db.RoomMediaStates.FindAsync(locationKey);
            if (state == null) return NotFound();
            
            // Calculate exactly how many milliseconds have passed since the HOST pushed this data.
            // By doing this on the server, we completely eliminate client clock drift issues!
            state.DataAgeMs = (DateTime.UtcNow - state.TimestampUtc).TotalMilliseconds;

            // Fetch LastFetchUtc before updating it, so the client knows if someone else fetched BEFORE them!
            var lastFetch = _lastFetchTimes.TryGetValue(locationKey, out var lf) ? lf : DateTime.MinValue;
            state.IdleTimeMs = lastFetch == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - lastFetch).TotalMilliseconds;
            _lastFetchTimes[locationKey] = DateTime.UtcNow;

            // AUTO ADVANCE QUEUE
            if (state.IsPlaying && state.DurationMs.HasValue && (state.DataAgeMs + state.TimecodeMs) >= state.DurationMs.Value)
            {
                var playlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(state.PlaylistJson);
                if (playlist != null && playlist.Count > 0)
                {
                    string nextUrl = null;
                    while (playlist.Count > 0)
                    {
                        var candidate = playlist[0];
                        playlist.RemoveAt(0);
                        if (!IsUrlBlacklisted(candidate))
                        {
                            nextUrl = candidate;
                            break;
                        }
                    }

                    if (nextUrl != null)
                    {
                        state.CurrentUrl = nextUrl;
                        state.PlaylistJson = System.Text.Json.JsonSerializer.Serialize(playlist);
                        
                        // Reset timings for the new video
                        state.TimecodeMs = 0;
                        state.TimestampUtc = DateTime.UtcNow;
                        state.DataAgeMs = 0;
                        state.DurationMs = null; // We don't know the duration of the new video yet!
                        
                        _db.RoomMediaStates.Update(state);
                        await RecordMediaPlay(state.CurrentUrl, locationKey, state.OwnerId);
                        await _db.SaveChangesAsync();
                    }
                    else
                    {
                        // No valid queue left, stop playing
                        state.IsPlaying = false;
                        state.TimecodeMs = (long)state.DurationMs.Value;
                        
                        _db.RoomMediaStates.Update(state);
                        await _db.SaveChangesAsync();
                    }
                }
                else
                {
                    // No queue left, stop playing
                    state.IsPlaying = false;
                    state.TimecodeMs = (long)state.DurationMs.Value;
                    
                    _db.RoomMediaStates.Update(state);
                    await _db.SaveChangesAsync();
                }
            }

            return Ok(state);
        }

        [HttpPost("{locationKey}/media")]
        public async Task<IActionResult> UpdateMediaState(string locationKey, [FromBody] RoomMediaStateSync state)
        {
            if (IsUrlBlacklisted(state.CurrentUrl))
            {
                return BadRequest("The provided URL is blacklisted.");
            }

            if (!string.IsNullOrEmpty(state.PlaylistJson))
            {
                try
                {
                    var playlist = System.Text.Json.JsonSerializer.Deserialize<List<string>>(state.PlaylistJson);
                    if (playlist != null && playlist.Any(url => IsUrlBlacklisted(url)))
                    {
                        return BadRequest("One or more URLs in the queue are blacklisted.");
                    }
                }
                catch { }
            }

            state.LocationKey = locationKey;
            
            // Check if any TV in the room is locked against this DJ.
            if (await IsRoomMediaLockedAsync(locationKey, state.OwnerId, state.BypassLock))
            {
                return StatusCode(403);
            }

            // Always stamp with the server's exact current time to prevent client drift
            state.TimestampUtc = DateTime.UtcNow;

            int retries = 3;
            bool isNewPlay = false;
            while (retries > 0)
            {
                try
                {
                    var existing = await _db.RoomMediaStates.FindAsync(locationKey);
                    isNewPlay = false;
                    
                    if (existing != null)
                    {
                        if (state.IsBackgroundSync && existing.OwnerId != state.OwnerId)
                        {
                            // Stale background push from a deposed DJ!
                            return Conflict();
                        }

                        if (existing.CurrentUrl != state.CurrentUrl && !string.IsNullOrEmpty(state.CurrentUrl))
                        {
                            isNewPlay = true;
                        }

                        existing.CurrentUrl = state.CurrentUrl;
                        existing.TimecodeMs = state.TimecodeMs;
                        existing.IsPlaying = state.IsPlaying;
                        existing.TimestampUtc = state.TimestampUtc;
                        existing.PlaylistJson = state.PlaylistJson;
                        existing.OwnerId = state.OwnerId;
                        existing.DurationMs = state.DurationMs;
                        _db.RoomMediaStates.Update(existing);
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(state.CurrentUrl))
                        {
                            isNewPlay = true;
                        }
                        
                        // We need a fresh instance to avoid EF tracking issues on retry
                        var newState = new RoomMediaStateSync {
                            LocationKey = state.LocationKey,
                            CurrentUrl = state.CurrentUrl,
                            TimecodeMs = state.TimecodeMs,
                            IsPlaying = state.IsPlaying,
                            TimestampUtc = state.TimestampUtc,
                            PlaylistJson = state.PlaylistJson,
                            OwnerId = state.OwnerId,
                            DurationMs = state.DurationMs
                        };
                        _db.RoomMediaStates.Add(newState);
                    }

                    await _db.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException)
                {
                    retries--;
                    if (retries == 0) throw;
                    _db.ChangeTracker.Clear();
                }
            }

            if (isNewPlay)
            {
                await RecordMediaPlay(state.CurrentUrl, locationKey, state.OwnerId);
                await _db.SaveChangesAsync(); // Save the MediaTrackRecord
            }
            
            if (!state.IsBackgroundSync)
            {
                _logger.LogInformation("MEDIA UPDATE: Room '{LocationKey}' is now playing '{CurrentUrl}' (DJ: {OwnerId})", locationKey, state.CurrentUrl, state.OwnerId);
            }
            return Ok(state);
        }
        [HttpPost("batch/tvs")]
        public async Task<IActionResult> GetTvsBatch([FromBody] List<string> locationKeys)
        {
            if (locationKeys == null || !locationKeys.Any()) return BadRequest();
            var tvs = await _db.TvPlacements
                .Where(t => locationKeys.Contains(t.LocationKey))
                .ToListAsync();
            return Ok(tvs);
        }

        [HttpPost("batch/media")]
        public async Task<IActionResult> GetMediaStatesBatch([FromBody] List<string> locationKeys)
        {
            if (locationKeys == null || !locationKeys.Any()) return BadRequest();
            var states = await _db.RoomMediaStates
                .Where(s => locationKeys.Contains(s.LocationKey))
                .ToListAsync();
                
            foreach (var state in states)
            {
                state.DataAgeMs = (DateTime.UtcNow - state.TimestampUtc).TotalMilliseconds;
                var lastFetch = _lastFetchTimes.TryGetValue(state.LocationKey, out var lf) ? lf : DateTime.MinValue;
                state.IdleTimeMs = lastFetch == DateTime.MinValue ? double.MaxValue : (DateTime.UtcNow - lastFetch).TotalMilliseconds;
                _lastFetchTimes[state.LocationKey] = DateTime.UtcNow;
            }
            return Ok(states);
        }

        [HttpGet("media/history")]
        public async Task<IActionResult> GetMediaHistory([FromQuery] int limit = 100)
        {
            var history = await _db.MediaTrackRecords
                .OrderByDescending(r => r.PlayedAtUtc)
                .Take(limit)
                .ToListAsync();
            return Ok(history);
        }

        [HttpGet("media/stats")]
        public async Task<IActionResult> GetMediaStats([FromQuery] int limit = 10)
        {
            var topUrls = await _db.MediaTrackRecords
                .GroupBy(r => r.Url)
                .Select(g => new { Url = g.Key, Count = g.Count(), LastPlayed = g.Max(r => r.PlayedAtUtc) })
                .OrderByDescending(x => x.Count)
                .Take(limit)
                .ToListAsync();

            var topDomains = await _db.MediaTrackRecords
                .GroupBy(r => r.Domain)
                .Select(g => new { Domain = g.Key, Count = g.Count(), LastPlayed = g.Max(r => r.PlayedAtUtc) })
                .OrderByDescending(x => x.Count)
                .Take(limit)
                .ToListAsync();

            return Ok(new { TopUrls = topUrls, TopDomains = topDomains });
        }

        private async Task RecordMediaPlay(string url, string locationKey, string ownerId)
        {
            if (string.IsNullOrEmpty(url)) return;

            string domain = string.Empty;
            try
            {
                var uri = new Uri(url);
                domain = uri.Host;
            }
            catch { }

            var record = new MediaTrackRecord
            {
                Url = url,
                Domain = domain,
                LocationKey = locationKey,
                OwnerId = ownerId,
                PlayedAtUtc = DateTime.UtcNow
            };

            _db.MediaTrackRecords.Add(record);
        }

        private bool IsUrlBlacklisted(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            
            var blacklistedDomains = _config.GetSection("MediaBlacklist:Domains").Get<List<string>>() ?? new List<string>();
            var blacklistedUrls = _config.GetSection("MediaBlacklist:Urls").Get<List<string>>() ?? new List<string>();
            var hashedDomains = _config.GetSection("MediaBlacklist:HashedDomains").Get<List<string>>() ?? new List<string>();
            var hashedUrls = _config.GetSection("MediaBlacklist:HashedUrls").Get<List<string>>() ?? new List<string>();

            if (blacklistedUrls.Contains(url, StringComparer.OrdinalIgnoreCase)) return true;
            if (hashedUrls.Any())
            {
                var urlHash = ComputeSha256Hash(url.ToLowerInvariant());
                if (hashedUrls.Contains(urlHash, StringComparer.OrdinalIgnoreCase)) return true;
            }

            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                
                if (blacklistedDomains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }

                if (hashedDomains.Any())
                {
                    var parts = host.Split('.');
                    for (int i = 0; i < parts.Length - 1; i++) // Need at least domain.tld
                    {
                        var domainToTest = string.Join(".", parts.Skip(i));
                        var domainHash = ComputeSha256Hash(domainToTest);
                        if (hashedDomains.Contains(domainHash, StringComparer.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private string ComputeSha256Hash(string rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                var builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}
