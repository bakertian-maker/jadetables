using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class SelfPlaySource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Source",
                table: "ImportedGames",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "ImportedGames");
        }
    }
}
