using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SettingsAndRatings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LetterPieces",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowOnLeaderboard",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SoundOn",
                table: "AspNetUsers",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "PlayerRatings",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GameType = table.Column<byte>(type: "tinyint", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: false),
                    Games = table.Column<int>(type: "int", nullable: false),
                    Wins = table.Column<int>(type: "int", nullable: false),
                    Draws = table.Column<int>(type: "int", nullable: false),
                    Losses = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2(0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayerRatings", x => new { x.UserId, x.GameType });
                    table.ForeignKey(
                        name: "FK_PlayerRatings_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayerRatings_GameType_Rating",
                table: "PlayerRatings",
                columns: new[] { "GameType", "Rating" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayerRatings");

            migrationBuilder.DropColumn(
                name: "LetterPieces",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ShowOnLeaderboard",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SoundOn",
                table: "AspNetUsers");
        }
    }
}
