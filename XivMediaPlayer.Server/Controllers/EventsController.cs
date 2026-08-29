using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;

namespace XivMediaPlayer.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EventsController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<EventsController> _logger;

        public EventsController(AppDbContext db, ILogger<EventsController> logger)
        {
            _db = db;
            _logger = logger;
        }

        private async Task<string?> GetAuthenticatedDiscordIdAsync()
        {
            if (Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                string raw = authHeader.ToString();
                if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    string token = raw.Substring(7).Trim();
                    using var sha256 = System.Security.Cryptography.SHA256.Create();
                    string hashedToken = Convert.ToHexString(sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

                    var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == hashedToken);
                    if (session != null && session.ExpiresUtc > DateTime.UtcNow)
                    {
                        session.LastUsedUtc = DateTime.UtcNow;
                        return session.DiscordId;
                    }
                }
            }
            return null;
        }

        [HttpGet]
        public async Task<IActionResult> GetEvents([FromQuery] string? datacenter = null, [FromQuery] string? world = null)
        {
            var now = DateTime.UtcNow;

            // Purge expired events past their end date
            try
            {
                var expired = await _db.WatchPartyEvents.Where(e => e.EndTimeUtc < now).ToListAsync();
                if (expired.Count > 0)
                {
                    _db.WatchPartyEvents.RemoveRange(expired);
                    await _db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanup expired watch party events");
            }

            var query = _db.WatchPartyEvents.Where(e => e.EndTimeUtc >= now);

            if (!string.IsNullOrWhiteSpace(datacenter))
            {
                query = query.Where(e => e.DataCenter.ToLower() == datacenter.Trim().ToLower());
            }

            if (!string.IsNullOrWhiteSpace(world))
            {
                query = query.Where(e => e.World.ToLower() == world.Trim().ToLower());
            }

            var events = await query.OrderBy(e => e.StartTimeUtc).ToListAsync();
            return Ok(events);
        }

        [HttpPost]
        public async Task<IActionResult> CreateEvent([FromBody] WatchPartyEvent watchEvent)
        {
            string? discordId = await GetAuthenticatedDiscordIdAsync();
            if (string.IsNullOrEmpty(discordId))
            {
                return Unauthorized("Discord login required to post a watch party event.");
            }

            if (string.IsNullOrWhiteSpace(watchEvent.Title))
            {
                return BadRequest("Title is required.");
            }

            if (string.IsNullOrWhiteSpace(watchEvent.Id))
            {
                watchEvent.Id = Guid.NewGuid().ToString("N");
            }

            watchEvent.DiscordOwnerId = discordId;
            watchEvent.CreatedAtUtc = DateTime.UtcNow;

            if (watchEvent.EndTimeUtc <= watchEvent.StartTimeUtc)
            {
                watchEvent.EndTimeUtc = watchEvent.StartTimeUtc.AddHours(2);
            }

            _db.WatchPartyEvents.Add(watchEvent);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Watch party event created: '{Title}' ({Id}) at {World} by Discord {DiscordId}",
                watchEvent.Title, watchEvent.Id, watchEvent.World, discordId);

            return Ok(watchEvent);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteEvent(string id)
        {
            string? discordId = await GetAuthenticatedDiscordIdAsync();
            if (string.IsNullOrEmpty(discordId))
            {
                return Unauthorized("Discord login required.");
            }

            var watchEvent = await _db.WatchPartyEvents.FindAsync(id);
            if (watchEvent == null)
            {
                return NotFound();
            }

            if (!string.Equals(watchEvent.DiscordOwnerId, discordId, StringComparison.OrdinalIgnoreCase))
            {
                return Forbid();
            }

            _db.WatchPartyEvents.Remove(watchEvent);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Watch party event deleted: '{Title}' ({Id})", watchEvent.Title, watchEvent.Id);
            return Ok();
        }
    }
}
