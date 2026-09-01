using MeuralManager.Web.Components;
using MeuralManager.Web.Services;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Persisted so browser-side sessions (see WebSessionStore) survive a container restart -
// without this, ASP.NET Core's Data Protection keys are ephemeral per-process and every
// restart would silently log everyone out instead of just failing to decrypt gracefully.
var keysPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH")
    ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MeuralManager.Web");

builder.Services.AddScoped<ProtectedLocalStorage>();
builder.Services.AddScoped<WebSessionStore>();
builder.Services.AddScoped<UserPreferencesStore>();
builder.Services.AddScoped<MeuralSessionState>();
builder.Services.AddSingleton<BackupArchiveService>();
builder.Services.AddSingleton<StaticAssetVersion>();
builder.Services.AddSingleton<ImageCacheManager>();
builder.Services.AddHostedService<BackupCleanupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

// In Development, disable caching for static files outright - the "?v=" query string on
// css/app.css and js/splitter.js (see App.razor / StaticAssetVersion) only changes when a
// file's mtime changes, which isn't a reliable enough signal moment-to-moment while iterating
// (e.g. editing under a debugger without a full rebuild), and having to hard-refresh to see a
// JS/CSS change is exactly the friction this exists to remove.
if (app.Environment.IsDevelopment())
{
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        }
    });
}
else
{
    app.UseStaticFiles();
}

app.UseAntiforgery();

app.MapBackupEndpoints();
app.MapImageCacheEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
