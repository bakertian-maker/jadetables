using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GameType = table.Column<byte>(type: "tinyint", nullable: false),
                    RoomCode = table.Column<string>(type: "char(5)", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "datetime2(0)", nullable: false),
                    Result = table.Column<byte>(type: "tinyint", nullable: false),
                    Reason = table.Column<byte>(type: "tinyint", nullable: false),
                    PlyCount = table.Column<short>(type: "smallint", nullable: false),
                    Moves = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpeningBook",
                columns: table => new
                {
                    PositionHash = table.Column<long>(type: "bigint", nullable: false),
                    Move = table.Column<short>(type: "smallint", nullable: false),
                    RedWins = table.Column<int>(type: "int", nullable: false),
                    BlackWins = table.Column<int>(type: "int", nullable: false),
                    Draws = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpeningBook", x => new { x.PositionHash, x.Move })
                        .Annotation("SqlServer:Clustered", true);
                });

            migrationBuilder.CreateTable(
                name: "GamePlayers",
                columns: table => new
                {
                    GameId = table.Column<long>(type: "bigint", nullable: false),
                    Seat = table.Column<byte>(type: "tinyint", nullable: false),
                    Side = table.Column<byte>(type: "tinyint", nullable: true),
                    PlayerKey = table.Column<byte[]>(type: "binary(16)", nullable: true),
                    IsBot = table.Column<bool>(type: "bit", nullable: false),
                    BotLevel = table.Column<byte>(type: "tinyint", nullable: true),
                    Score = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GamePlayers", x => new { x.GameId, x.Seat });
                    table.ForeignKey(
                        name: "FK_GamePlayers_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GamePlayers_PlayerKey",
                table: "GamePlayers",
                column: "PlayerKey",
                filter: "[PlayerKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Games_GameType_EndedAt",
                table: "Games",
                columns: new[] { "GameType", "EndedAt" });

            // Opening book updates: one call per finished game with all its opening moves.
            migrationBuilder.Sql("""
                CREATE TYPE dbo.BookMoveList AS TABLE
                (
                    PositionHash bigint   NOT NULL,
                    Move         smallint NOT NULL,
                    PRIMARY KEY (PositionHash, Move)
                );
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE dbo.Book_RecordGame
                    @Moves  dbo.BookMoveList READONLY,
                    @Result tinyint              -- 1 Red wins, 2 Black wins, 3 draw
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    DECLARE @red   int = IIF(@Result = 1, 1, 0),
                            @black int = IIF(@Result = 2, 1, 0),
                            @draw  int = IIF(@Result = 3, 1, 0);

                    BEGIN TRANSACTION;

                    -- Existing (position, move) rows: add this game's result.
                    UPDATE b
                       SET b.RedWins   += @red,
                           b.BlackWins += @black,
                           b.Draws     += @draw
                      FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                      JOIN @Moves AS m
                        ON m.PositionHash = b.PositionHash AND m.Move = b.Move;

                    -- New rows. UPDLOCK + HOLDLOCK stop two games that end at the same moment
                    -- from inserting the same row twice.
                    INSERT dbo.OpeningBook (PositionHash, Move, RedWins, BlackWins, Draws)
                    SELECT m.PositionHash, m.Move, @red, @black, @draw
                      FROM @Moves AS m
                     WHERE NOT EXISTS (SELECT 1
                                         FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                                        WHERE b.PositionHash = m.PositionHash AND b.Move = m.Move);

                    COMMIT TRANSACTION;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.Book_RecordGame;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS dbo.BookMoveList;");

            migrationBuilder.DropTable(
                name: "GamePlayers");

            migrationBuilder.DropTable(
                name: "OpeningBook");

            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
