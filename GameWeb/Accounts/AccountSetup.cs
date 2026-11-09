using System.Security.Claims;
using GameWeb.Data;
using GameWeb.Localization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace GameWeb.Accounts;

/// <summary>
/// Player accounts with ASP.NET Core Identity: email and password, and "Sign in with Google" when a
/// client id is configured. Accounts need the database; without a connection string the site runs
/// without them and hides the sign-in links.
/// </summary>
public static class AccountSetup
{
    /// <summary>The claim holding the player's display name, so the header needs no database call.</summary>
    public const string DisplayNameClaim = "jt:display_name";

    public const string GoogleScheme = "Google";

    public static void AddAccounts(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var config = builder.Configuration;
        services.Configure<EmailOptions>(config.GetSection("Email"));
        services.AddSingleton<AccountEmails>();
        services.AddSingleton<AccountStore>();
        services.AddSingleton(new AccountFeatures(
            Enabled: true,
            Google: !string.IsNullOrWhiteSpace(config["Authentication:Google:ClientId"])));
        services.AddCascadingAuthenticationState();
        services.AddAuthorization();
        // Identity's stores use a context per request, from the same pool as the game archive.
        services.AddScoped(sp => sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<GamesDbContext>>().CreateDbContext());

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = IdentityConstants.ApplicationScheme;
            o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        });
        if (config["Authentication:Google:ClientId"] is { Length: > 0 } clientId)
        {
            auth.AddGoogle(GoogleScheme, o =>
            {
                o.ClientId = clientId;
                o.ClientSecret = config["Authentication:Google:ClientSecret"] ?? "";
                // Google sends people back to https://www.jadetables.com/signin-google: register exactly that.
                o.CallbackPath = "/signin-google";
            });
        }
        auth.AddIdentityCookies();

        bool confirmEmail = config.GetSection("Email").Get<EmailOptions>()?.IsConfigured ?? false;
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                // Players must confirm their address once email can be sent; until then they can play straight away.
                o.SignIn.RequireConfirmedAccount = confirmEmail;
                o.Password.RequiredLength = 8;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddEntityFrameworkStores<GamesDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders()
            .AddErrorDescriber<LocalizedIdentityErrors>()
            .AddClaimsPrincipalFactory<DisplayNameClaimsFactory>();

        services.ConfigureExternalCookie(o => o.Cookie.Path = "/");
        services.ConfigureApplicationCookie(o =>
        {
            o.LoginPath = "/account/login";
            o.Cookie.Name = "jt.auth";
            // Without this the cookie would only cover the language the player signed in under (/zh-cn),
            // because the language prefix is the request's PathBase.
            o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
        });
    }

    public static void MapAccountEndpoints(this WebApplication app)
    {
        // Sign out. The form also clears the browser's player id (Scripts/gameweb.ts), so whoever uses
        // this browser next doesn't add games to this account.
        app.MapPost("/account/logout", async (SignInManager<ApplicationUser> signIn, HttpContext http, [FromForm] string? returnUrl) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect(SafeReturn(http, returnUrl));
        });

        // "Sign in with Google": always at the root address, so Google sends players back to the one
        // registered address (/signin-google) whichever language they started in.
        app.MapPost("/account/external-login", (HttpContext http, SignInManager<ApplicationUser> signIn,
            [FromForm] string provider, [FromForm] string? returnUrl, [FromForm] string? playerId) =>
        {
            var redirect = "/account/external-callback?returnUrl=" + Uri.EscapeDataString(returnUrl ?? "/");
            var props = signIn.ConfigureExternalAuthenticationProperties(provider, redirect);
            if (!string.IsNullOrEmpty(playerId)) props.Items["playerId"] = playerId;
            return TypedResults.Challenge(props, [provider]);
        });

        app.MapGet("/account/external-callback", async (HttpContext http, string? returnUrl,
            SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users, AccountStore store) =>
        {
            var back = Languages.IsSafeLocalPath(returnUrl) ? returnUrl! : "/";
            var prefix = LanguagePrefixOf(back);
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null) return Results.LocalRedirect(prefix + "/account/login?error=external");
            string? playerId = info.AuthenticationProperties?.Items.TryGetValue("playerId", out var pid) == true ? pid : null;

            var result = await signIn.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true, bypassTwoFactor: true);
            ApplicationUser? user;
            if (result.Succeeded)
            {
                user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
            }
            else if (result.IsLockedOut)
            {
                return Results.LocalRedirect(prefix + "/account/login?error=locked");
            }
            else
            {
                var email = info.Principal.FindFirstValue(ClaimTypes.Email);
                if (string.IsNullOrEmpty(email)) return Results.LocalRedirect(prefix + "/account/login?error=external");
                user = await users.FindByEmailAsync(email);
                if (user is null)
                {
                    user = new ApplicationUser
                    {
                        UserName = email,
                        Email = email,
                        EmailConfirmed = true,   // Google has checked it
                        DisplayName = CleanDisplayName(info.Principal.FindFirstValue(ClaimTypes.GivenName)
                            ?? info.Principal.FindFirstValue(ClaimTypes.Name), email),
                        CreatedAt = DateTime.UtcNow,
                        Culture = CultureOfPrefix(prefix),
                    };
                    if (!(await users.CreateAsync(user)).Succeeded)
                        return Results.LocalRedirect(prefix + "/account/login?error=external");
                }
                else if (!user.EmailConfirmed)
                {
                    // Someone registered this address with a password but never confirmed it. Google has just
                    // shown the address belongs to this person, so the unconfirmed password can't be trusted.
                    await users.RemovePasswordAsync(user);
                    user.EmailConfirmed = true;
                    await users.UpdateAsync(user);
                }
                if (!(await users.AddLoginAsync(user, info)).Succeeded)
                    return Results.LocalRedirect(prefix + "/account/login?error=external");
                await signIn.SignInAsync(user, isPersistent: true, info.LoginProvider);
            }
            if (user is not null)
            {
                await store.LinkAsync(user.Id, playerId);
                // Continue in the player's own language (their account remembers it).
                if (Languages.Supported.Contains(user.Culture))
                {
                    LanguagePaths.RememberChoice(http.Response, user.Culture);
                    back = LanguagePaths.InLanguage(back, user.Culture);
                }
            }
            return Results.LocalRedirect(back);
        });
    }

    /// <summary>A local return address, or the home page of the current language.</summary>
    public static string SafeReturn(HttpContext http, string? returnUrl) =>
        Languages.IsSafeLocalPath(returnUrl) ? returnUrl! : http.Request.PathBase + "/";

    static string LanguagePrefixOf(string path)
    {
        foreach (var (p, _) in LanguagePaths.Prefixes)
            if (path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) || path.Equals(p, StringComparison.OrdinalIgnoreCase))
                return p;
        return "";
    }

    static string CultureOfPrefix(string prefix) =>
        LanguagePaths.Prefixes.FirstOrDefault(p => p.Prefix == prefix).Culture ?? Languages.English;

    /// <summary>A display name of 1–20 characters; falls back to the part of the email before the @.</summary>
    public static string CleanDisplayName(string? name, string? email)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) name = (email ?? "").Split('@')[0].Trim();
        if (name.Length == 0) name = "Player";
        return name.Length > 20 ? name[..20] : name;
    }
}

/// <summary>What the account features can do in this run, for the UI.</summary>
public sealed record AccountFeatures(bool Enabled, bool Google)
{
    public static readonly AccountFeatures Off = new(false, false);
}

/// <summary>Adds the display name to the sign-in cookie.</summary>
public sealed class DisplayNameClaimsFactory(UserManager<ApplicationUser> users, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AccountSetup.DisplayNameClaim, user.DisplayName));
        return identity;
    }
}

/// <summary>Identity's error messages, translated. Only the ones players can actually meet.</summary>
public sealed class LocalizedIdentityErrors(IStringLocalizer<SharedResource> l) : IdentityErrorDescriber
{
    IdentityError E(string code, string text) => new() { Code = code, Description = text };

    public override IdentityError DefaultError() => E(nameof(DefaultError), l["Something went wrong. Please try again."]);
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), l["There is already an account with {0}.", email]);
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), l["There is already an account with {0}.", userName]);
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), l["{0} isn't a valid email address.", email ?? ""]);
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), l["{0} isn't a valid email address.", userName ?? ""]);
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), l["Passwords need at least {0} characters.", length]);
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), l["That password isn't right."]);
    public override IdentityError InvalidToken() => E(nameof(InvalidToken), l["This link has expired or was already used. Please ask for a new one."]);
    public override IdentityError LoginAlreadyAssociated() => E(nameof(LoginAlreadyAssociated), l["That Google account is already linked to another Jade Tables account."]);
}
