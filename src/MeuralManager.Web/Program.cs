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
builder.Services.AddSingleton<ImageCacheManager>();
builder.Services.AddSingleton<ImmichProxyRegistry>();
builder.Services.AddHostedService<BackupCleanupService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

// Serves wwwroot with content-fingerprinted URLs (App.razor references them through Assets[...]),
// so a redeploy can't leave a browser on a stale CSS/JS file.
app.MapStaticAssets();

app.MapBackupEndpoints();
app.MapImageCacheEndpoints();
app.MapImmichEndpoints();
app.MapHomepageEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
