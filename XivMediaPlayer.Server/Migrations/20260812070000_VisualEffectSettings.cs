using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class VisualEffectSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<float>(
                name: "EffectIntensity",
                table: "TvPlacements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.65f);

            migrationBuilder.AddColumn<float>(
                name: "EffectSpeed",
                table: "TvPlacements",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0f);

            migrationBuilder.AddColumn<int>(
                name: "VisualEffectMode",
                table: "TvPlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<float>(
                name: "EffectIntensity",
                table: "BannerPlacements",
                type: "REAL",
                nullable: false,
                defaultValue: 0.65f);

            migrationBuilder.AddColumn<float>(
                name: "EffectSpeed",
                table: "BannerPlacements",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0f);

            migrationBuilder.AddColumn<int>(
                name: "VisualEffectMode",
                table: "BannerPlacements",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EffectIntensity",
                table: "TvPlacements");

            migrationBuilder.DropColumn(
                name: "EffectSpeed",
                table: "TvPlacements");

            migrationBuilder.DropColumn(
                name: "VisualEffectMode",
                table: "TvPlacements");

            migrationBuilder.DropColumn(
                name: "EffectIntensity",
                table: "BannerPlacements");

            migrationBuilder.DropColumn(
                name: "EffectSpeed",
                table: "BannerPlacements");

            migrationBuilder.DropColumn(
                name: "VisualEffectMode",
                table: "BannerPlacements");
        }
    }
}
