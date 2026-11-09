using System.Security.Claims;
using GameWeb.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace GameWeb.Accounts;

/// <summary>The signed-in player, as the game pages see them.</summary>
public sealed record SignedInPlayer(string UserId, string DisplayName);

/// <summary>
/// Used by the lobby and room pages: who is signed in (so their account name is used at the table), and
/// tying this browser's player id to their account so the games they finish appear in their history.
/// Does nothing for guests or when the site runs without accounts.
/// </summary>
public sealed class PlayerAccountLinker(IServiceProvider services)
{
    SignedInPlayer? _player;
    bool _looked;

    /// <summary>The signed-in player, or null for a guest.</summary>
    public async Task<SignedInPlayer?> CurrentAsync()
    {
        if (_looked) return _player;
        _looked = true;
        try
        {
            if (services.GetService<AccountStore>() is null
                || services.GetService<AuthenticationStateProvider>() is not { } auth) return null;
            var user = (await auth.GetAuthenticationStateAsync()).User;
            if (user.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId)
                _player = new SignedInPlayer(userId, user.FindFirstValue(AccountSetup.DisplayNameClaim) ?? "");
        }
        catch (InvalidOperationException)
        {
            // No authentication state available (accounts off).
        }
        return _player;
    }

    public async Task LinkCurrentAsync(string playerId)
    {
        if (await CurrentAsync() is { } player && services.GetService<AccountStore>() is { } store)
            await store.LinkAsync(player.UserId, playerId);
    }
}
