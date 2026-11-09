using Microsoft.AspNetCore.Identity;

namespace GameWeb.Data;

/// <summary>A registered player. Signs in with email and password, Google, or both.</summary>
public sealed class ApplicationUser : IdentityUser
{
    /// <summary>The name shown on the site; defaults to the part of the email before the @.</summary>
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>The player's language: emails are sent in it, and logging in switches the site to it.</summary>
    public string Culture { get; set; } = "en";
    /// <summary>"Your turn" chime when the tab is in the background.</summary>
    public bool SoundOn { get; set; } = true;
    /// <summary>Xiangqi pieces show letters instead of Chinese characters.</summary>
    public bool LetterPieces { get; set; }
    /// <summary>Listed on the public leaderboard under the display name.</summary>
    public bool ShowOnLeaderboard { get; set; } = true;
}

/// <summary>A player's rating in one game (Elo). Only games against the computer or another account count.</summary>
public sealed class PlayerRating
{
    public string UserId { get; set; } = "";
    public global::GameWeb.Games.Archive.GameType GameType { get; set; }
    public int Rating { get; set; } = Ratings.Start;
    public int Games { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Connects a browser's player id to an account. Games are saved with a hash of the browser's player id
/// (see <see cref="SqlGameStore.PlayerKey"/>), so linking a browser gives the account every game played
/// in it, before and after registering. A browser belongs to one account; signing out gives the browser a
/// new id, so the next person to use it doesn't add to someone else's history.
/// </summary>
public sealed class PlayerLink
{
    /// <summary>First 16 bytes of SHA-256 of the browser's player id.</summary>
    public byte[] PlayerKey { get; set; } = [];
    public string UserId { get; set; } = "";
    public DateTime LinkedAt { get; set; }
}
