using System.ComponentModel.DataAnnotations.Schema;

namespace XivMediaPlayer.Server.Models
{
    public class TvPlacement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string LocationKey { get; set; } = string.Empty;
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public int ScaleAspectMode { get; set; } = 0;
        public string OwnerId { get; set; } = string.Empty;
        public bool IsLocked { get; set; } = false;

        public float Opacity { get; set; } = 1.0f;
        public bool IsProjectorMode { get; set; } = false;

        public float ScreensaverColorR { get; set; } = 0.0f;
        public float ScreensaverColorG { get; set; } = 0.0f;
        public float ScreensaverColorB { get; set; } = 0.0f;
        public int ScreensaverStyle { get; set; } = 0;
        public string IdleBrandingUrl { get; set; } = string.Empty;
        public int VisualEffectMode { get; set; } = 0;
        public float EffectIntensity { get; set; } = 0.65f;
        public float EffectSpeed { get; set; } = 1.0f;

        public string? DiscordOwnerId { get; set; }
        public string? AllowedDiscordOwnerIdsJson { get; set; }
        public DateTime? LastOwnerActivityUtc { get; set; }

        public bool IsDiscordUserAuthorized(string? discordId, List<string>? callerPlayerHashes = null)
        {
            if (string.IsNullOrEmpty(DiscordOwnerId)) return true;
            if (string.IsNullOrEmpty(discordId)) return false;

            if (string.Equals(DiscordOwnerId, discordId, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(AllowedDiscordOwnerIdsJson))
            {
                try
                {
                    var allowed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(AllowedDiscordOwnerIdsJson);
                    if (allowed != null)
                    {
                        if (allowed.Any(id => string.Equals(id, discordId, StringComparison.OrdinalIgnoreCase))) return true;
                        if (callerPlayerHashes != null && callerPlayerHashes.Any(ph => allowed.Any(id => string.Equals(id, ph, StringComparison.OrdinalIgnoreCase)))) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        [NotMapped]
        public bool BypassLock { get; set; } = false;

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    }

    public class DiscordUser
    {
        public string DiscordId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string PlayerHashesJson { get; set; } = "[]";
        public string AvatarUrl { get; set; } = string.Empty;
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public List<string> GetPlayerHashes()
        {
            if (string.IsNullOrEmpty(PlayerHashesJson)) return new List<string>();
            try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(PlayerHashesJson) ?? new List<string>(); }
            catch { return new List<string>(); }
        }
    }

    public class UserSession
    {
        public string Token { get; set; } = Guid.NewGuid().ToString("N");
        public string DiscordId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresUtc { get; set; } = DateTime.UtcNow.AddDays(60);
    }

    public class BotApiKey
    {
        public string KeyHash { get; set; } = string.Empty;
        public string DiscordId { get; set; } = string.Empty;
        public string Label { get; set; } = "Bot Key";
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastUsedUtc { get; set; }
        public bool IsRevoked { get; set; } = false;
    }

    public class RoomClaimRequest
    {
        public string LocationKey { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
    }

    public class RoomMediaStateSync
    {
        public string LocationKey { get; set; } = string.Empty;
        public string CurrentUrl { get; set; } = string.Empty;
        public long TimecodeMs { get; set; }
        public bool IsPlaying { get; set; } = true;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
        public string PlaylistJson { get; set; } = "[]";
        public string OwnerId { get; set; } = string.Empty;

        [NotMapped]
        public bool BypassLock { get; set; } = false;

        [NotMapped]
        public bool IsBackgroundSync { get; set; } = false;

        [NotMapped]
        public double DataAgeMs { get; set; } = 0;

        [NotMapped]
        public double IdleTimeMs { get; set; } = 0;

        public double? DurationMs { get; set; } = null;
    }

    public class MediaTrackRecord
    {
        public int Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public string LocationKey { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Room-level venue settings (idle branding, etc.).</summary>
    public class RoomVenueSettings
    {
        public string LocationKey { get; set; } = string.Empty;
        public string IdleBrandingUrl { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;
        public string? DiscordOwnerId { get; set; }
        public string? AllowedDiscordOwnerIdsJson { get; set; }
        public DateTime? LastOwnerActivityUtc { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public bool IsDiscordUserAuthorized(string? discordId, List<string>? callerPlayerHashes = null)
        {
            if (string.IsNullOrEmpty(DiscordOwnerId)) return true;
            if (string.IsNullOrEmpty(discordId)) return false;

            if (string.Equals(DiscordOwnerId, discordId, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(AllowedDiscordOwnerIdsJson))
            {
                try
                {
                    var allowed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(AllowedDiscordOwnerIdsJson);
                    if (allowed != null)
                    {
                        if (allowed.Any(id => string.Equals(id, discordId, StringComparison.OrdinalIgnoreCase))) return true;
                        if (callerPlayerHashes != null && callerPlayerHashes.Any(ph => allowed.Any(id => string.Equals(id, ph, StringComparison.OrdinalIgnoreCase)))) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        [NotMapped]
        public bool BypassLock { get; set; } = false;
    }

    /// <summary>Static image banner prop, not a TV; no playback.</summary>
    public class BannerPlacement
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string LocationKey { get; set; } = string.Empty;
        public float PositionX { get; set; }
        public float PositionY { get; set; }
        public float PositionZ { get; set; }
        public float RotationX { get; set; }
        public float RotationY { get; set; }
        public float RotationZ { get; set; }
        public float ScaleX { get; set; }
        public float ScaleY { get; set; }
        public string ImageUrl { get; set; } = string.Empty;
        public float Opacity { get; set; } = 1.0f;
        public int VisualEffectMode { get; set; } = 0;
        public float EffectIntensity { get; set; } = 0.65f;
        public float EffectSpeed { get; set; } = 1.0f;
        public string OwnerId { get; set; } = string.Empty;
        public string? DiscordOwnerId { get; set; }
        public string? AllowedDiscordOwnerIdsJson { get; set; }
        public DateTime? LastOwnerActivityUtc { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        public bool IsDiscordUserAuthorized(string? discordId, List<string>? callerPlayerHashes = null)
        {
            if (string.IsNullOrEmpty(DiscordOwnerId)) return true;
            if (string.IsNullOrEmpty(discordId)) return false;

            if (string.Equals(DiscordOwnerId, discordId, StringComparison.OrdinalIgnoreCase)) return true;

            if (!string.IsNullOrEmpty(AllowedDiscordOwnerIdsJson))
            {
                try
                {
                    var allowed = System.Text.Json.JsonSerializer.Deserialize<List<string>>(AllowedDiscordOwnerIdsJson);
                    if (allowed != null)
                    {
                        if (allowed.Any(id => string.Equals(id, discordId, StringComparison.OrdinalIgnoreCase))) return true;
                        if (callerPlayerHashes != null && callerPlayerHashes.Any(ph => allowed.Any(id => string.Equals(id, ph, StringComparison.OrdinalIgnoreCase)))) return true;
                    }
                }
                catch { }
            }
            return false;
        }

        [NotMapped]
        public bool BypassLock { get; set; } = false;
    }

    public class WatchPartyEvent
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string BannerUrl { get; set; } = string.Empty;
        public string LocationKey { get; set; } = string.Empty;
        public string DataCenter { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public string HousingZone { get; set; } = string.Empty;
        public int Ward { get; set; }
        public int Plot { get; set; }
        public int Room { get; set; }
        public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
        public DateTime EndTimeUtc { get; set; } = DateTime.UtcNow.AddHours(2);
        public string DiscordOwnerId { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<TvPlacement> TvPlacements { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<RoomMediaStateSync> RoomMediaStates { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<MediaTrackRecord> MediaTrackRecords { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<RoomVenueSettings> RoomVenueSettings { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<BannerPlacement> BannerPlacements { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<DiscordUser> DiscordUsers { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<UserSession> UserSessions { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<BotApiKey> BotApiKeys { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<WatchPartyEvent> WatchPartyEvents { get; set; } = null!;

        public AppDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<AppDbContext> options) : base(options) { }

        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TvPlacement>()
                .HasKey(t => t.Id);

            modelBuilder.Entity<TvPlacement>()
                .HasIndex(t => t.LocationKey);

            modelBuilder.Entity<TvPlacement>()
                .Property(t => t.Id)
                .IsRequired();

            modelBuilder.Entity<TvPlacement>()
                .Property(t => t.LocationKey)
                .IsRequired();

            modelBuilder.Entity<RoomMediaStateSync>()
                .HasKey(m => m.LocationKey);

            modelBuilder.Entity<MediaTrackRecord>()
                .HasKey(r => r.Id);

            modelBuilder.Entity<RoomVenueSettings>()
                .HasKey(v => v.LocationKey);

            modelBuilder.Entity<BannerPlacement>()
                .HasKey(b => b.Id);

            modelBuilder.Entity<BannerPlacement>()
                .HasIndex(b => b.LocationKey);

            modelBuilder.Entity<DiscordUser>()
                .HasKey(u => u.DiscordId);

            modelBuilder.Entity<UserSession>()
                .HasKey(s => s.Token);

            modelBuilder.Entity<UserSession>()
                .HasIndex(s => s.DiscordId);

            modelBuilder.Entity<BotApiKey>()
                .HasKey(b => b.KeyHash);

            modelBuilder.Entity<BotApiKey>()
                .HasIndex(b => b.DiscordId);

            modelBuilder.Entity<WatchPartyEvent>()
                .HasKey(e => e.Id);

            modelBuilder.Entity<WatchPartyEvent>()
                .HasIndex(e => e.DataCenter);

            modelBuilder.Entity<WatchPartyEvent>()
                .HasIndex(e => e.World);
        }
    }
}
