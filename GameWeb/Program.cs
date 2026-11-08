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
}
else
{
    builder.Services.AddSingleton<IGameArchive>(NullGameStore.Instance);
    builder.Services.AddSingleton<IOpeningBook>(NullGameStore.Instance);
}
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddScoped<GameText>();
builder.Services.AddScoped<XqText>();

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
app.UseRewriter(new RewriteOptions()
    .AddRedirectToWwwPermanent()
);

app.UseCookiePolicy();
// Render errors at the requested URL, retaining the HTTP status and original language.
app.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");

app.MapStaticAssets(); // fingerprinted URLs for CSS/JS so browsers never keep stale files

// Language: the ?culture= query, then the language cookie, then the browser's Accept-Language.
app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture(Languages.English)
    .AddSupportedCultures(Languages.Supported)
    .AddSupportedUICultures(Languages.Supported));
app.UseAntiforgery();

// Language switch: stores the choice in the standard ASP.NET Core culture cookie and returns to the page.
app.MapGet("/culture/set", (string culture, string? redirectUri, HttpContext http) =>
{
    if (!Languages.Supported.Contains(culture)) culture = Languages.English;
    http.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax, HttpOnly = true });
    return Results.LocalRedirect(Languages.IsSafeLocalPath(redirectUri) ? redirectUri! : "/");
});

// robots.txt and sitemap.xml for search engines.
app.MapSearchFiles();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
