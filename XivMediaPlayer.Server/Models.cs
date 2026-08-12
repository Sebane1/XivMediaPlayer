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

        [NotMapped]
        public bool BypassLock { get; set; } = false;

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
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
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

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
        public string OwnerId { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        [NotMapped]
        public bool BypassLock { get; set; } = false;
    }

    public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public Microsoft.EntityFrameworkCore.DbSet<TvPlacement> TvPlacements { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<RoomMediaStateSync> RoomMediaStates { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<MediaTrackRecord> MediaTrackRecords { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<RoomVenueSettings> RoomVenueSettings { get; set; } = null!;
        public Microsoft.EntityFrameworkCore.DbSet<BannerPlacement> BannerPlacements { get; set; } = null!;

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
        }
    }
}
