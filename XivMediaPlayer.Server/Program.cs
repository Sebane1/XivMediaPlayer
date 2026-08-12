using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Bind to all interfaces so external connections work via port forwarding
builder.WebHost.UseUrls("http://0.0.0.0:5000");

// Add services to the container.

builder.Services.AddControllers();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Data Source=XivMediaPlayer.db;Cache=Shared;";

builder.Services.AddDbContext<XivMediaPlayer.Server.Models.AppDbContext>(options =>
    options.UseSqlite(connectionString));

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
    }

    db.Database.Migrate();
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
