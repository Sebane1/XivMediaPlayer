using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class VenueBrandingAndBanners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RoomVenueSettings",
                columns: table => new
                {
                    LocationKey = table.Column<string>(type: "TEXT", nullable: false),
                    IdleBrandingUrl = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoomVenueSettings", x => x.LocationKey);
                });

            migrationBuilder.CreateTable(
                name: "BannerPlacements",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LocationKey = table.Column<string>(type: "TEXT", nullable: false),
                    PositionX = table.Column<float>(type: "REAL", nullable: false),
                    PositionY = table.Column<float>(type: "REAL", nullable: false),
                    PositionZ = table.Column<float>(type: "REAL", nullable: false),
                    RotationX = table.Column<float>(type: "REAL", nullable: false),
                    RotationY = table.Column<float>(type: "REAL", nullable: false),
                    RotationZ = table.Column<float>(type: "REAL", nullable: false),
                    ScaleX = table.Column<float>(type: "REAL", nullable: false),
                    ScaleY = table.Column<float>(type: "REAL", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: false),
                    Opacity = table.Column<float>(type: "REAL", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BannerPlacements", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BannerPlacements_LocationKey",
                table: "BannerPlacements",
                column: "LocationKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BannerPlacements");
            migrationBuilder.DropTable(name: "RoomVenueSettings");
        }
    }
}
