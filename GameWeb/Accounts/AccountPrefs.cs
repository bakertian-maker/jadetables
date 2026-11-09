using GameWeb.Data;

namespace GameWeb.Accounts;

/// <summary>
/// Game settings stored on the account (chime, letter pieces), for the room pages. Safe to use when the
/// site runs without accounts: reads return nothing and saves do nothing.
/// </summary>
public sealed class AccountPrefs(IServiceProvider services)
{
    public Task<(bool SoundOn, bool LetterPieces)?> PrefsAsync(string userId) =>
        services.GetService<AccountStore>() is { } store ? store.PrefsAsync(userId) : Task.FromResult<(bool, bool)?>(null);

    public Task SavePrefsAsync(string userId, bool? soundOn = null, bool? letterPieces = null) =>
        services.GetService<AccountStore>() is { } store ? store.SavePrefsAsync(userId, soundOn, letterPieces) : Task.CompletedTask;
}
