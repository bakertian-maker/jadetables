using System.Security.Claims;
using GameWeb.Data;
using Microsoft.AspNetCore.Components.Authorization;

namespace GameWeb.Accounts;

/// <summary>
/// Used by the room pages: when the player is signed in, ties this browser's player id to their account,
/// so the games they finish here appear in their history. Does nothing for guests or without accounts.
/// </summary>
public sealed class PlayerAccountLinker(IServiceProvider services)
{
    public async Task LinkCurrentAsync(string playerId)
    {
        try
        {
            if (services.GetService<AccountStore>() is not { } store
                || services.GetService<AuthenticationStateProvider>() is not { } auth) return;
            var state = await auth.GetAuthenticationStateAsync();
            if (state.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId)
                await store.LinkAsync(userId, playerId);
        }
        catch (InvalidOperationException)
        {
            // No authentication state available (accounts off): nothing to link.
        }
    }
}
