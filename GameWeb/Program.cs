using System.Security.Claims;
using GameWeb.Accounts;
using GameWeb.Seo;
using GameWeb.Components;
using GameWeb.Data;
using GameWeb.Games.Archive;
using GameWeb.Games.Catalog;
using GameWeb.Games.Rooms;
using GameWeb.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Rewrite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Command-line tools (run instead of the website), e.g.:
//   dotnet run --project GameWeb -- import-xiangqi <file or folder> [--dry-run]
//   dotnet run --project GameWeb -- selfplay-xiangqi [--games 100] [--budget 300] [--parallel N] [--dry-run]
//   dotnet run --project GameWeb -- tune-xiangqi [--iterations 600] [--out weights.json] | --adopt weights.json
//   dotnet run --project GameWeb -- match-xiangqi --candidate weights.json [--games 200] [--budget 100]
if (args.Length > 0 && args[0] == XiangqiImportCommand.Name)
{
    Environment.ExitCode = await XiangqiImportCommand.RunAsync(args[1..], builder.Configuration);
    return;
}
if (args.Length > 0 && args[0] == XiangqiSelfPlayCommand.Name)
{
    Environment.ExitCode = await XiangqiSelfPlayCommand.RunAsync(args[1..], builder.Configuration);
    return;
}
if (args.Length > 0 && args[0] == XiangqiTuningCommands.Tune)
{
    Environment.ExitCode = await XiangqiTuningCommands.TuneAsync(args[1..], builder.Configuration);
    return;
}
if (args.Length > 0 && args[0] == XiangqiTuningCommands.Match)
{
    Environment.ExitCode = await XiangqiTuningCommands.MatchAsync(args[1..]);
    return;
}

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<GameCatalog>();
builder.Services.AddSingleton<RoomService>();
builder.Services.AddScoped<PlayerAccountLinker>();
builder.Services.AddScoped<AccountPrefs>();
// One antiforgery cookie for the whole site, not one per language prefix: forms such as "Continue with
// Google" post from /zh-cn/... to an address at the root.
builder.Services.AddAntiforgery(o => o.Cookie.Path = "/");
// Browsers remember to use HTTPS for a year (the default is 30 days).
builder.Services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));

// Game archive and opening book in SQL Server. The connection string comes from user secrets in
// development and from the environment in production; without one, games simply aren't saved.
var gamesDb = builder.Configuration.GetConnectionString("Games");
if (!string.IsNullOrWhiteSpace(gamesDb))
{
    builder.Services.AddPooledDbContextFactory<GamesDbContext>(o =>
        o.UseSqlServer(gamesDb, sql => sql.EnableRetryOnFailure(maxRetryCount: 3).CommandTimeout(15)));
    builder.Services.AddSingleton<SqlGameStore>();
    builder.Services.AddSingleton<IGameArchive>(sp => sp.GetRequiredService<SqlGameStore>());
    builder.Services.AddSingleton<IOpeningBook>(sp => sp.GetRequiredService<SqlGameStore>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SqlGameStore>());
    // Player accounts (register, log in, game history) live in the same database.
    builder.AddAccounts();
}
else
{
    builder.Services.AddSingleton<IGameArchive>(NullGameStore.Instance);
    builder.Services.AddSingleton<IOpeningBook>(NullGameStore.Instance);
    builder.Services.AddSingleton(AccountFeatures.Off);
}
bool accounts = !string.IsNullOrWhiteSpace(gamesDb);
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddScoped<GameText>();
builder.Services.AddScoped<XqText>();
builder.Services.AddScoped<DdzText>();

var app = builder.Build();

// Development: bring the database schema up to date on startup. In production, apply migrations
// as a deployment step instead (dotnet ef migrations script).
if (!string.IsNullOrWhiteSpace(gamesDb) && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    using var db = app.Services.GetRequiredService<IDbContextFactory<GamesDbContext>>().CreateDbContext();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
// Basic security headers on every response. (IIS's own Server and X-Powered-By headers are removed in web.config.)
app.Use((http, next) =>
{
    var headers = http.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    return next(http);
});
app.UseRewriter(new RewriteOptions()
    .AddRedirectToWwwPermanent()
);

// Language from the address (/zh-cn/..., /zh-hk/...); see LanguagePaths. Routing must come after it,
// so pages and files are matched on the address without the prefix.
app.UseLanguagePaths();
app.UseRouting();
if (accounts)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseCookiePolicy();
// Render errors at the requested URL, retaining the HTTP status and original language.
app.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");

app.MapStaticAssets(); // fingerprinted URLs for CSS/JS so browsers never keep stale files

// Language: from the address only, so each address always shows the same language.
var localization = new RequestLocalizationOptions()
    .SetDefaultCulture(Languages.English)
    .AddSupportedCultures(Languages.Supported)
    .AddSupportedUICultures(Languages.Supported);
localization.RequestCultureProviders = [new LanguagePaths.PathCultureProvider()];
app.UseRequestLocalization(localization);
app.UseAntiforgery();

// Language switch: remembers the choice in the standard culture cookie (so English addresses send this
// visitor to their language later) and goes to the same page in the new language.
app.MapGet("/culture/set", async (string culture, string? redirectUri, HttpContext http) =>
{
    if (!Languages.Supported.Contains(culture)) culture = Languages.English;
    LanguagePaths.RememberChoice(http.Response, culture);
    // Signed in: the choice follows the account to other devices.
    if (http.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) is { Length: > 0 } userId
        && http.RequestServices.GetService<AccountStore>() is { } store)
        await store.SaveCultureAsync(userId, culture);
    var page = Languages.IsSafeLocalPath(redirectUri) ? redirectUri! : "/";
    return Results.LocalRedirect(LanguagePaths.PrefixFor(culture) + page);
});

if (accounts) app.MapAccountEndpoints();

// robots.txt and sitemap.xml for search engines.
app.MapSearchFiles();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
