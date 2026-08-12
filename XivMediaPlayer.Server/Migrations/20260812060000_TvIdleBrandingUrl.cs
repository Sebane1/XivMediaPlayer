using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace XivMediaPlayer.Server.Migrations
{
    /// <inheritdoc />
    public partial class TvIdleBrandingUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdleBrandingUrl",
                table: "TvPlacements",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdleBrandingUrl",
                table: "TvPlacements");
        }
    }
}
