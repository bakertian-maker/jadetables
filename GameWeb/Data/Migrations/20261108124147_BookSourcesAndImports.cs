using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GameWeb.Data.Migrations
{
    /// <inheritdoc />
    public partial class BookSourcesAndImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_OpeningBook",
                table: "OpeningBook");

            migrationBuilder.AddColumn<byte>(
                name: "Source",
                table: "OpeningBook",
                type: "tinyint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_OpeningBook",
                table: "OpeningBook",
                columns: new[] { "PositionHash", "Move", "Source" })
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateTable(
                name: "ImportedGames",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ContentHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: true),
                    OpeningName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Result = table.Column<byte>(type: "tinyint", nullable: false),
                    PlyCount = table.Column<short>(type: "smallint", nullable: false),
                    Moves = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    ImportedAt = table.Column<DateTime>(type: "datetime2(0)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportedGames", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportedGames_ContentHash",
                table: "ImportedGames",
                column: "ContentHash",
                unique: true);

            // Games played on the site count under Source = 0.
            migrationBuilder.Sql(RecordGameProcedure("ALTER", sourceAware: true));

            // Imports: counts already added up per (position, move) for a batch of games.
            migrationBuilder.Sql("""
                CREATE TYPE dbo.BookCountList AS TABLE
                (
                    PositionHash bigint   NOT NULL,
                    Move         smallint NOT NULL,
                    RedWins      int      NOT NULL,
                    BlackWins    int      NOT NULL,
                    Draws        int      NOT NULL,
                    PRIMARY KEY (PositionHash, Move)
                );
                """);

            migrationBuilder.Sql("""
                CREATE PROCEDURE dbo.Book_AddCounts
                    @Source tinyint,
                    @Counts dbo.BookCountList READONLY
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SET XACT_ABORT ON;

                    BEGIN TRANSACTION;

                    UPDATE b
                       SET b.RedWins   += c.RedWins,
                           b.BlackWins += c.BlackWins,
                           b.Draws     += c.Draws
                      FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                      JOIN @Counts AS c
                        ON c.PositionHash = b.PositionHash AND c.Move = b.Move AND b.Source = @Source;

                    INSERT dbo.OpeningBook (PositionHash, Move, Source, RedWins, BlackWins, Draws)
                    SELECT c.PositionHash, c.Move, @Source, c.RedWins, c.BlackWins, c.Draws
                      FROM @Counts AS c
                     WHERE NOT EXISTS (SELECT 1
                                         FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                                        WHERE b.PositionHash = c.PositionHash AND b.Move = c.Move AND b.Source = @Source);

                    COMMIT TRANSACTION;
                END
                """);
        }

        /// <summary>dbo.Book_RecordGame, before (sourceAware: false) and after this migration.</summary>
        static string RecordGameProcedure(string verb, bool sourceAware)
        {
            string match = sourceAware ? " AND b.Source = 0" : "";
            string columns = sourceAware ? "PositionHash, Move, Source, RedWins, BlackWins, Draws" : "PositionHash, Move, RedWins, BlackWins, Draws";
            string values = sourceAware ? "m.PositionHash, m.Move, 0, @red, @black, @draw" : "m.PositionHash, m.Move, @red, @black, @draw";
            return $"""
                {verb} PROCEDURE dbo.Book_RecordGame
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

                    UPDATE b
                       SET b.RedWins   += @red,
                           b.BlackWins += @black,
                           b.Draws     += @draw
                      FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                      JOIN @Moves AS m
                        ON m.PositionHash = b.PositionHash AND m.Move = b.Move{match};

                    INSERT dbo.OpeningBook ({columns})
                    SELECT {values}
                      FROM @Moves AS m
                     WHERE NOT EXISTS (SELECT 1
                                         FROM dbo.OpeningBook AS b WITH (UPDLOCK, HOLDLOCK)
                                        WHERE b.PositionHash = m.PositionHash AND b.Move = m.Move{match});

                    COMMIT TRANSACTION;
                END
                """;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS dbo.Book_AddCounts;");
            migrationBuilder.Sql("DROP TYPE IF EXISTS dbo.BookCountList;");
            // Imported rows can't survive without the Source column.
            migrationBuilder.Sql("DELETE FROM dbo.OpeningBook WHERE Source <> 0;");
            migrationBuilder.Sql(RecordGameProcedure("ALTER", sourceAware: false));

            migrationBuilder.DropTable(
                name: "ImportedGames");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OpeningBook",
                table: "OpeningBook");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "OpeningBook");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OpeningBook",
                table: "OpeningBook",
                columns: new[] { "PositionHash", "Move" })
                .Annotation("SqlServer:Clustered", true);
        }
    }
}
