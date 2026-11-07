using GameWeb.Components;
using GameWeb.Games.Catalog;
using GameWeb.Games.Rooms;
using GameWeb.Localization;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Rewrite;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<GameCatalog>();
builder.Services.AddSingleton<RoomService>();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddScoped<GameText>();
builder.Services.AddScoped<XqText>();

var app = builder.Build();

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

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
