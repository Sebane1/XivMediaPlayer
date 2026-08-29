using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Bind to all interfaces so external connections work via port forwarding
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Add services to the container.

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Data Source=XivMediaPlayer.db;Cache=Shared;";

builder.Services.AddDbContext<XivMediaPlayer.Server.Models.AppDbContext>(options =>
    options.UseSqlite(connectionString)
           .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

builder.Services.AddHostedService<XivMediaPlayer.Server.Services.HouseClaimCleanupService>();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Ensure database is created and apply migrations safely
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<XivMediaPlayer.Server.Models.AppDbContext>();
    
    // Check if the old pre-migrations table exists
    var tables = db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='TvPlacements'").ToList();
    
    if (tables.Any()) {
        db.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" TEXT NOT NULL CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY,
                ""ProductVersion"" TEXT NOT NULL
            );
        ");

        var history = db.Database.SqlQueryRaw<string>("SELECT MigrationId FROM __EFMigrationsHistory").ToList();

        if (!history.Contains("20260606020813_InitialCreate")) {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260606020813_InitialCreate', '10.0.8');");
        }

        // Check for DurationMs
        var roomCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('RoomMediaStates') WHERE name='DurationMs'").ToList();
        if (roomCols.Any() && !history.Contains("20260606020852_AddDurationMs")) {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260606020852_AddDurationMs', '10.0.8');");
        }

        // Check for IsProjectorMode
        var projCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='IsProjectorMode'").ToList();
        if (projCols.Any() && !history.Contains("20260622043315_AddProjectorSettings")) {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260622043315_AddProjectorSettings', '10.0.8');");
        }

        // Check for ScreensaverStyle
        var ssCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='ScreensaverStyle'").ToList();
        if (ssCols.Any() && !history.Contains("20260622061404_AddScreensaverSettings")) {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260622061404_AddScreensaverSettings', '10.0.8');");
        }
        // Multi-TV schema already applied outside EF history (manual deploy / table rebuild).
        var idPkCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE pk = 1").ToList();
        if (idPkCols.Any() && idPkCols[0] == "Id" && !history.Contains("20260812030000_MultiTvPlacements"))
        {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260812030000_MultiTvPlacements', '10.0.8');");
        }

        var venueTables = db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='RoomVenueSettings'").ToList();
        if (venueTables.Any() && !history.Contains("20260812040000_VenueBrandingAndBanners"))
        {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260812040000_VenueBrandingAndBanners', '10.0.8');");
        }

        var scaleAspectCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='ScaleAspectMode'").ToList();
        if (scaleAspectCols.Any() && !history.Contains("20260812050000_TvScaleAspectMode"))
        {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260812050000_TvScaleAspectMode', '10.0.8');");
        }

        var idleBrandingCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='IdleBrandingUrl'").ToList();
        if (idleBrandingCols.Any() && !history.Contains("20260812060000_TvIdleBrandingUrl"))
        {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260812060000_TvIdleBrandingUrl', '10.0.8');");
        }

        var tvFxCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='EffectIntensity'").ToList();
        if (tables.Any() && !tvFxCols.Any())
        {
            db.Database.ExecuteSqlRaw("ALTER TABLE \"TvPlacements\" ADD COLUMN \"VisualEffectMode\" INTEGER NOT NULL DEFAULT 0;");
            db.Database.ExecuteSqlRaw("ALTER TABLE \"TvPlacements\" ADD COLUMN \"EffectIntensity\" REAL NOT NULL DEFAULT 0.65;");
            db.Database.ExecuteSqlRaw("ALTER TABLE \"TvPlacements\" ADD COLUMN \"EffectSpeed\" REAL NOT NULL DEFAULT 1.0;");
        }

        var bannerTables = db.Database.SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table' AND name='BannerPlacements'").ToList();
        if (bannerTables.Any())
        {
            var bannerFxCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('BannerPlacements') WHERE name='EffectIntensity'").ToList();
            if (!bannerFxCols.Any())
            {
                db.Database.ExecuteSqlRaw("ALTER TABLE \"BannerPlacements\" ADD COLUMN \"VisualEffectMode\" INTEGER NOT NULL DEFAULT 0;");
                db.Database.ExecuteSqlRaw("ALTER TABLE \"BannerPlacements\" ADD COLUMN \"EffectIntensity\" REAL NOT NULL DEFAULT 0.65;");
                db.Database.ExecuteSqlRaw("ALTER TABLE \"BannerPlacements\" ADD COLUMN \"EffectSpeed\" REAL NOT NULL DEFAULT 1.0;");
            }
        }

        var visualFxCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('TvPlacements') WHERE name='EffectIntensity'").ToList();
        if (visualFxCols.Any() && !history.Contains("20260812070000_VisualEffectSettings"))
        {
            db.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('20260812070000_VisualEffectSettings', '10.0.8');");
        }

        // Robust migration: Check each table and individual column separately
        void EnsureColumnExists(string tableName, string columnName, string columnDef)
        {
            try
            {
                var tbls = db.Database.SqlQueryRaw<string>($"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'").ToList();
                if (tbls.Any())
                {
                    var cols = db.Database.SqlQueryRaw<string>($"SELECT name FROM pragma_table_info('{tableName}') WHERE name='{columnName}'").ToList();
                    if (!cols.Any())
                    {
                        db.Database.ExecuteSqlRaw($"ALTER TABLE \"{tableName}\" ADD COLUMN \"{columnName}\" {columnDef};");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Migration check note for {tableName}.{columnName}: {ex.Message}");
            }
        }

        EnsureColumnExists("TvPlacements", "DiscordOwnerId", "TEXT NULL");
        EnsureColumnExists("TvPlacements", "LastOwnerActivityUtc", "TEXT NULL");
        EnsureColumnExists("TvPlacements", "AllowedDiscordOwnerIdsJson", "TEXT NULL");

        EnsureColumnExists("RoomVenueSettings", "DiscordOwnerId", "TEXT NULL");
        EnsureColumnExists("RoomVenueSettings", "LastOwnerActivityUtc", "TEXT NULL");
        EnsureColumnExists("RoomVenueSettings", "AllowedDiscordOwnerIdsJson", "TEXT NULL");

        EnsureColumnExists("BannerPlacements", "DiscordOwnerId", "TEXT NULL");
        EnsureColumnExists("BannerPlacements", "LastOwnerActivityUtc", "TEXT NULL");
        EnsureColumnExists("BannerPlacements", "AllowedDiscordOwnerIdsJson", "TEXT NULL");
    }

    // Create Discord auth tables if they don't exist
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""DiscordUsers"" (
            ""DiscordId"" TEXT NOT NULL CONSTRAINT ""PK_DiscordUsers"" PRIMARY KEY,
            ""Username"" TEXT NOT NULL DEFAULT '',
            ""PlayerHashesJson"" TEXT NOT NULL DEFAULT '[]',
            ""AvatarUrl"" TEXT NOT NULL DEFAULT '',
            ""LastSeenUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""CreatedAtUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'
        );
    ");

    // Migrate: add PlayerHashesJson column if missing (existing DBs)
    var phCols = db.Database.SqlQueryRaw<string>("SELECT name FROM pragma_table_info('DiscordUsers') WHERE name='PlayerHashesJson'").ToList();
    if (!phCols.Any())
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"DiscordUsers\" ADD COLUMN \"PlayerHashesJson\" TEXT NOT NULL DEFAULT '[]';");
    }

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""UserSessions"" (
            ""Token"" TEXT NOT NULL CONSTRAINT ""PK_UserSessions"" PRIMARY KEY,
            ""DiscordId"" TEXT NOT NULL DEFAULT '',
            ""CreatedAtUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""LastUsedUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""ExpiresUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""BotApiKeys"" (
            ""KeyHash"" TEXT NOT NULL CONSTRAINT ""PK_BotApiKeys"" PRIMARY KEY,
            ""DiscordId"" TEXT NOT NULL DEFAULT '',
            ""Label"" TEXT NOT NULL DEFAULT 'Bot Key',
            ""CreatedAtUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""LastUsedUtc"" TEXT NULL,
            ""IsRevoked"" INTEGER NOT NULL DEFAULT 0
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""WatchPartyEvents"" (
            ""Id"" TEXT NOT NULL CONSTRAINT ""PK_WatchPartyEvents"" PRIMARY KEY,
            ""Title"" TEXT NOT NULL DEFAULT '',
            ""Description"" TEXT NOT NULL DEFAULT '',
            ""BannerUrl"" TEXT NOT NULL DEFAULT '',
            ""LocationKey"" TEXT NOT NULL DEFAULT '',
            ""DataCenter"" TEXT NOT NULL DEFAULT '',
            ""World"" TEXT NOT NULL DEFAULT '',
            ""HousingZone"" TEXT NOT NULL DEFAULT '',
            ""Ward"" INTEGER NOT NULL DEFAULT 0,
            ""Plot"" INTEGER NOT NULL DEFAULT 0,
            ""Room"" INTEGER NOT NULL DEFAULT 0,
            ""StartTimeUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""EndTimeUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""DiscordOwnerId"" TEXT NOT NULL DEFAULT '',
            ""CreatedAtUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'
        );
    ");

    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""MediaTrackRecords"" (
            ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_MediaTrackRecords"" PRIMARY KEY AUTOINCREMENT,
            ""LocationKey"" TEXT NOT NULL DEFAULT '',
            ""Url"" TEXT NOT NULL DEFAULT '',
            ""Domain"" TEXT NOT NULL DEFAULT '',
            ""PlayedAtUtc"" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00',
            ""OwnerId"" TEXT NOT NULL DEFAULT ''
        );
    ");

    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Database migration warning/fallback: {ex.Message}");
        db.Database.EnsureCreated();
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
