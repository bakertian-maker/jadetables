using GameWeb.Games.Archive;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace GameWeb.Data;

/// <summary>One finished game (a Xiangqi game, or a whole Mahjong match).</summary>
public sealed class GameRecord
{
    public long Id { get; set; }
    public GameType GameType { get; set; }
    public string RoomCode { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    /// <summary>Xiangqi: 1 Red wins, 2 Black wins, 3 draw. Mahjong: 1 match completed.</summary>
    public byte Result { get; set; }
    /// <summary>Xiangqi: how it ended (checkmate, resignation…). Mahjong: 0.</summary>
    public byte Reason { get; set; }
    /// <summary>Xiangqi: half-moves played. Mahjong: hands played.</summary>
    public short PlyCount { get; set; }
    /// <summary>Xiangqi moves in coordinate notation, space-separated: "h2e2 h9g7 …".</summary>
    public string? Moves { get; set; }
    /// <summary>Optional JSON for detail that is rarely queried (e.g. Mahjong settings).</summary>
    public string? Details { get; set; }
    public List<GamePlayerRecord> Players { get; set; } = [];
}

public sealed class GamePlayerRecord
{
    public long GameId { get; set; }
    public byte Seat { get; set; }
    /// <summary>Xiangqi: 0 Red, 1 Black. Mahjong: null.</summary>
    public byte? Side { get; set; }
    /// <summary>First 16 bytes of SHA-256 of the browser's player id; never the id itself.</summary>
    public byte[]? PlayerKey { get; set; }
    public bool IsBot { get; set; }
    public byte? BotLevel { get; set; }
    /// <summary>Mahjong final score; null for Xiangqi.</summary>
    public int? Score { get; set; }
}

public enum BookSource : byte
{
    /// <summary>Games played on GameWeb.</summary>
    Played = 0,
    /// <summary>Games imported from game record files.</summary>
    Imported = 1,
    /// <summary>Games the computer played against itself.</summary>
    SelfPlay = 2,
}

/// <summary>
/// Opening book: how often each move was played from a position, and how those games ended.
/// Counts are kept per source, so imported data can be removed or rebuilt without touching
/// the statistics of games played on the site; lookups add the sources together.
/// </summary>
public sealed class BookEntry
{
    /// <summary>Zobrist hash of the position (side to move included).</summary>
    public long PositionHash { get; set; }
    /// <summary>from * 90 + to.</summary>
    public short Move { get; set; }
    public BookSource Source { get; set; }
    public int RedWins { get; set; }
    public int BlackWins { get; set; }
    public int Draws { get; set; }
}

/// <summary>
/// A game from outside the site: imported from a record file, or played by the computer against itself.
/// Kept so its part of the book can be rebuilt without the original files.
/// </summary>
public sealed class ImportedGameRecord
{
    public long Id { get; set; }
    /// <summary><see cref="BookSource.Imported"/> or <see cref="BookSource.SelfPlay"/>.</summary>
    public BookSource Source { get; set; } = BookSource.Imported;
    /// <summary>SHA-256 of the result and moves: the same game is imported only once.</summary>
    public byte[] ContentHash { get; set; } = [];
    public string? FileName { get; set; }
    public string? OpeningName { get; set; }
    /// <summary>1 Red wins, 2 Black wins, 3 draw.</summary>
    public byte Result { get; set; }
    public short PlyCount { get; set; }
    public string Moves { get; set; } = "";
    public DateTime ImportedAt { get; set; }
}

/// <summary>Games, the opening book and player accounts (ASP.NET Core Identity tables, AspNet*).</summary>
public sealed class GamesDbContext(DbContextOptions<GamesDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<GameRecord> Games => Set<GameRecord>();
    public DbSet<GamePlayerRecord> GamePlayers => Set<GamePlayerRecord>();
    public DbSet<BookEntry> OpeningBook => Set<BookEntry>();
    public DbSet<ImportedGameRecord> ImportedGames => Set<ImportedGameRecord>();
    public DbSet<PlayerLink> PlayerLinks => Set<PlayerLink>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);   // the Identity tables

        model.Entity<ApplicationUser>(e =>
        {
            e.Property(u => u.DisplayName).HasMaxLength(20).IsRequired();
            e.Property(u => u.CreatedAt).HasColumnType("datetime2(0)");
            e.Property(u => u.Culture).HasMaxLength(10).IsRequired();
        });

        model.Entity<PlayerLink>(e =>
        {
            e.ToTable("PlayerLinks");
            e.HasKey(l => l.PlayerKey);
            e.Property(l => l.PlayerKey).HasColumnType("binary(16)");
            e.Property(l => l.LinkedAt).HasColumnType("datetime2(0)");
            e.HasIndex(l => l.UserId);
            // Deleting an account removes its links; its games stay, with nobody attached.
            e.HasOne<ApplicationUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GameRecord>(e =>
        {
            e.ToTable("Games");
            e.Property(g => g.GameType).HasConversion<byte>();
            e.Property(g => g.RoomCode).HasColumnType("char(5)").IsRequired();
            e.Property(g => g.StartedAt).HasColumnType("datetime2(0)");
            e.Property(g => g.EndedAt).HasColumnType("datetime2(0)");
            e.Property(g => g.Moves).HasColumnType("varchar(max)").IsUnicode(false);
            e.Property(g => g.Details).HasColumnType("nvarchar(max)");
            // Recent games of one type, e.g. for history pages and statistics.
            e.HasIndex(g => new { g.GameType, g.EndedAt });
            e.HasMany(g => g.Players).WithOne().HasForeignKey(p => p.GameId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GamePlayerRecord>(e =>
        {
            e.ToTable("GamePlayers");
            e.HasKey(p => new { p.GameId, p.Seat });
            e.Property(p => p.PlayerKey).HasColumnType("binary(16)");
            // A player's own game history.
            e.HasIndex(p => p.PlayerKey).HasFilter("[PlayerKey] IS NOT NULL");
        });

        model.Entity<BookEntry>(e =>
        {
            e.ToTable("OpeningBook");
            // Clustered on the lookup key: one index seek returns every move for a position.
            e.HasKey(b => new { b.PositionHash, b.Move, b.Source }).IsClustered();
            e.Property(b => b.Source).HasConversion<byte>();
        });

        model.Entity<ImportedGameRecord>(e =>
        {
            e.ToTable("ImportedGames");
            e.Property(g => g.ContentHash).HasColumnType("binary(32)");
            e.HasIndex(g => g.ContentHash).IsUnique();
            e.Property(g => g.FileName).HasMaxLength(260);
            e.Property(g => g.OpeningName).HasMaxLength(100);
            e.Property(g => g.Moves).HasColumnType("varchar(max)").IsUnicode(false);
            e.Property(g => g.ImportedAt).HasColumnType("datetime2(0)");
            e.Property(g => g.Source).HasConversion<byte>().HasDefaultValue(BookSource.Imported).HasSentinel(BookSource.Played);
        });
    }
}
