using System.Text;
using GameWeb.Accounts;
using GameWeb.Data;
using GameWeb.Localization;
using GameWeb.Seo;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;

namespace GameWeb.Components.Account;

/// <summary>
/// Shared plumbing for the account pages. They render on the server without a live connection (plain
/// HTML forms), because signing in has to set a cookie on an ordinary HTTP response. Services are looked
/// up lazily: without a database the site has no accounts, and the pages say so instead of failing.
/// </summary>
public abstract class AccountPageBase : ComponentBase
{
    [Inject] protected IServiceProvider Services { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected AccountFeatures Features { get; set; } = default!;
    [Inject] protected IConfiguration Config { get; set; } = default!;
    [CascadingParameter] protected HttpContext? HttpContext { get; set; }

    protected UserManager<ApplicationUser> Users => Services.GetRequiredService<UserManager<ApplicationUser>>();
    protected SignInManager<ApplicationUser> SignIn => Services.GetRequiredService<SignInManager<ApplicationUser>>();
    protected AccountStore Store => Services.GetRequiredService<AccountStore>();
    protected AccountEmails Emails => Services.GetRequiredService<AccountEmails>();

    protected readonly List<string> Errors = [];

    protected bool SignedIn => HttpContext?.User.Identity?.IsAuthenticated == true;

    /// <summary>Goes to a page of the site in the current language ("account", "xiangqi/room/ABCDE").</summary>
    protected void Go(string relative) => Nav.NavigateTo(relative.Length == 0 ? "./" : relative);

    /// <summary>
    /// After signing in: switch to the player's own language (their account remembers it) and go on to
    /// <paramref name="relative"/> in that language.
    /// </summary>
    protected void GoAfterSignIn(ApplicationUser user, string relative)
    {
        var culture = Languages.Supported.Contains(user.Culture) ? user.Culture : Languages.CurrentCulture;
        GameWeb.Localization.LanguagePaths.RememberChoice(HttpContext!.Response, culture);
        if (culture == Languages.CurrentCulture) Go(relative);
        else Nav.NavigateTo(GameWeb.Localization.LanguagePaths.InLanguage("/" + relative, culture));
    }

    /// <summary>A return address from the query, if it is a page of this site; otherwise <paramref name="fallback"/>.</summary>
    protected static string SafeRelative(string? returnUrl, string fallback)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return fallback;
        var r = returnUrl.TrimStart('/');
        bool ok = !r.Contains("//") && !r.Contains('\\') && !r.Contains(':') && !r.Any(char.IsControl);
        return ok ? r : fallback;
    }

    /// <summary>A full link into the site in the current language, for emails.</summary>
    protected string AbsoluteLink(string relative)
    {
        var request = HttpContext!.Request;
        return SearchFiles.BaseUrl(Config, request) + request.PathBase + "/" + relative;
    }

    protected static string EncodeToken(string token) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

    protected static string? DecodeToken(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }

    protected async Task SendConfirmationAsync(ApplicationUser user)
    {
        var token = await Users.GenerateEmailConfirmationTokenAsync(user);
        var link = AbsoluteLink($"account/confirm-email?userId={Uri.EscapeDataString(user.Id)}&code={EncodeToken(token)}");
        await Emails.SendConfirmationAsync(user, link);
    }
}
