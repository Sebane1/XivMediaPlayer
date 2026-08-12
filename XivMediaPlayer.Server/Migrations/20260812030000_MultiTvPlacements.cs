using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class MultiTvPlacements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite cannot alter primary keys in place. Rebuild the table with Id as PK.
            // Existing rows keep LocationKey and placement data; missing Ids are backfilled first.
            migrationBuilder.Sql(@"
                UPDATE TvPlacements
                SET Id = (
                    lower(hex(randomblob(4))) || '-' ||
                    lower(hex(randomblob(2))) || '-4' ||
                    substr(lower(hex(randomblob(2))), 1, 3) || '-' ||
                    substr('89ab', abs(random()) % 4 + 1, 1) || substr(lower(hex(randomblob(2))), 1, 3) || '-' ||
                    lower(hex(randomblob(6)))
                )
                WHERE Id IS NULL OR trim(Id) = '';
            ");

            migrationBuilder.Sql(@"
                CREATE TABLE TvPlacements_new (
                    Id TEXT NOT NULL PRIMARY KEY,
                    LocationKey TEXT NOT NULL,
                    PositionX REAL NOT NULL,
                    PositionY REAL NOT NULL,
                    PositionZ REAL NOT NULL,
                    RotationX REAL NOT NULL,
                    RotationY REAL NOT NULL,
                    RotationZ REAL NOT NULL,
                    ScaleX REAL NOT NULL,
                    ScaleY REAL NOT NULL,
                    OwnerId TEXT NOT NULL,
                    IsLocked INTEGER NOT NULL,
                    LastUpdated TEXT NOT NULL,
                    Opacity REAL NOT NULL DEFAULT 1.0,
                    IsProjectorMode INTEGER NOT NULL DEFAULT 0,
                    ScreensaverColorR REAL NOT NULL DEFAULT 0.0,
                    ScreensaverColorG REAL NOT NULL DEFAULT 0.0,
                    ScreensaverColorB REAL NOT NULL DEFAULT 0.0,
                    ScreensaverStyle INTEGER NOT NULL DEFAULT 0
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO TvPlacements_new (
                    Id, LocationKey, PositionX, PositionY, PositionZ,
                    RotationX, RotationY, RotationZ, ScaleX, ScaleY,
                    OwnerId, IsLocked, LastUpdated, Opacity, IsProjectorMode,
                    ScreensaverColorR, ScreensaverColorG, ScreensaverColorB, ScreensaverStyle
                )
                SELECT
                    Id, LocationKey, PositionX, PositionY, PositionZ,
                    RotationX, RotationY, RotationZ, ScaleX, ScaleY,
                    OwnerId, IsLocked, LastUpdated, Opacity, IsProjectorMode,
                    ScreensaverColorR, ScreensaverColorG, ScreensaverColorB, ScreensaverStyle
                FROM TvPlacements;
            ");

            migrationBuilder.Sql(@"DROP TABLE TvPlacements;");
            migrationBuilder.Sql(@"ALTER TABLE TvPlacements_new RENAME TO TvPlacements;");
            migrationBuilder.Sql(@"CREATE INDEX IX_TvPlacements_LocationKey ON TvPlacements (LocationKey);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort rollback: keep one TV per LocationKey (most recently updated).
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS IX_TvPlacements_LocationKey;");

            migrationBuilder.Sql(@"
                CREATE TABLE TvPlacements_legacy (
                    LocationKey TEXT NOT NULL PRIMARY KEY,
                    Id TEXT NULL,
                    PositionX REAL NOT NULL,
                    PositionY REAL NOT NULL,
                    PositionZ REAL NOT NULL,
                    RotationX REAL NOT NULL,
                    RotationY REAL NOT NULL,
                    RotationZ REAL NOT NULL,
                    ScaleX REAL NOT NULL,
                    ScaleY REAL NOT NULL,
                    OwnerId TEXT NOT NULL,
                    IsLocked INTEGER NOT NULL,
                    LastUpdated TEXT NOT NULL,
                    Opacity REAL NOT NULL DEFAULT 1.0,
                    IsProjectorMode INTEGER NOT NULL DEFAULT 0,
                    ScreensaverColorR REAL NOT NULL DEFAULT 0.0,
                    ScreensaverColorG REAL NOT NULL DEFAULT 0.0,
                    ScreensaverColorB REAL NOT NULL DEFAULT 0.0,
                    ScreensaverStyle INTEGER NOT NULL DEFAULT 0
                );
            ");

            migrationBuilder.Sql(@"
                INSERT INTO TvPlacements_legacy (
                    LocationKey, Id, PositionX, PositionY, PositionZ,
                    RotationX, RotationY, RotationZ, ScaleX, ScaleY,
                    OwnerId, IsLocked, LastUpdated, Opacity, IsProjectorMode,
                    ScreensaverColorR, ScreensaverColorG, ScreensaverColorB, ScreensaverStyle
                )
                SELECT
                    LocationKey, Id, PositionX, PositionY, PositionZ,
                    RotationX, RotationY, RotationZ, ScaleX, ScaleY,
                    OwnerId, IsLocked, LastUpdated, Opacity, IsProjectorMode,
                    ScreensaverColorR, ScreensaverColorG, ScreensaverColorB, ScreensaverStyle
                FROM TvPlacements t
                WHERE t.LastUpdated = (
                    SELECT MAX(t2.LastUpdated) FROM TvPlacements t2 WHERE t2.LocationKey = t.LocationKey
                )
                GROUP BY LocationKey;
            ");

            migrationBuilder.Sql(@"DROP TABLE TvPlacements;");
            migrationBuilder.Sql(@"ALTER TABLE TvPlacements_legacy RENAME TO TvPlacements;");
        }
    }
}
