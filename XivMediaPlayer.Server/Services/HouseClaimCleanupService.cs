using Microsoft.EntityFrameworkCore;
using XivMediaPlayer.Server.Models;

namespace XivMediaPlayer.Server.Services
{
    public class HouseClaimCleanupService : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<HouseClaimCleanupService> _logger;
        private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
        private const double ClaimExpirationDays = 45.0;

        public HouseClaimCleanupService(IServiceProvider services, ILogger<HouseClaimCleanupService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HouseClaimCleanupService starting. Checking for 45-day inactive claims every {Hours} hours.", CheckInterval.TotalHours);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PerformCleanupAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during 45-day house claim cleanup.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }

        private async Task PerformCleanupAsync(CancellationToken cancellationToken)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var cutoff = DateTime.UtcNow.AddDays(-ClaimExpirationDays);

            // 1. Expire TV Placement claims older than 45 days
            var expiredTvs = await db.TvPlacements
                .Where(t => t.DiscordOwnerId != null && t.LastOwnerActivityUtc != null && t.LastOwnerActivityUtc < cutoff)
                .ToListAsync(cancellationToken);

            foreach (var tv in expiredTvs)
            {
                _logger.LogInformation("Claim expired for TV '{Id}' in location '{LocationKey}' (Discord Owner: {DiscordOwnerId}, Last Activity: {LastActivity}). Demolishing lock.",
                    tv.Id, tv.LocationKey, tv.DiscordOwnerId, tv.LastOwnerActivityUtc);

                tv.DiscordOwnerId = null;
                tv.IsLocked = false;
            }

            // 2. Expire Venue Settings claims older than 45 days
            var expiredVenues = await db.RoomVenueSettings
                .Where(v => v.DiscordOwnerId != null && v.LastOwnerActivityUtc != null && v.LastOwnerActivityUtc < cutoff)
                .ToListAsync(cancellationToken);

            foreach (var venue in expiredVenues)
            {
                _logger.LogInformation("Claim expired for Venue Settings in location '{LocationKey}' (Discord Owner: {DiscordOwnerId}, Last Activity: {LastActivity}).",
                    venue.LocationKey, venue.DiscordOwnerId, venue.LastOwnerActivityUtc);

                venue.DiscordOwnerId = null;
            }

            // 3. Expire Banner Placement claims older than 45 days
            var expiredBanners = await db.BannerPlacements
                .Where(b => b.DiscordOwnerId != null && b.LastOwnerActivityUtc != null && b.LastOwnerActivityUtc < cutoff)
                .ToListAsync(cancellationToken);

            foreach (var banner in expiredBanners)
            {
                _logger.LogInformation("Claim expired for Banner '{Id}' in location '{LocationKey}' (Discord Owner: {DiscordOwnerId}, Last Activity: {LastActivity}).",
                    banner.Id, banner.LocationKey, banner.DiscordOwnerId, banner.LastOwnerActivityUtc);

                banner.DiscordOwnerId = null;
            }

            if (expiredTvs.Count > 0 || expiredVenues.Count > 0 || expiredBanners.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("45-day claim cleanup complete. Released {TvCount} TVs, {VenueCount} Venue Settings, {BannerCount} Banners.",
                    expiredTvs.Count, expiredVenues.Count, expiredBanners.Count);
            }
        }
    }
}
