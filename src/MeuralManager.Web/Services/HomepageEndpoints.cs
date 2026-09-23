using System.Security.Cryptography;
using System.Text;
using MeuralManager.Core.Data;

namespace MeuralManager.Web.Services;

// Stats for a gethomepage.dev "customapi" widget. Homepage polls without anyone signed in, so
// instead of a session this takes a per-account token (generated on the Settings page) in an
// X-API-Key header and finds the account whose settings table holds that token's hash. Only the
// hash is stored - the endpoint never needs the token back, just to recognise it - so a copy of
// the DB can't be used to call it, and losing the Data Protection keys doesn't break it.
public static class HomepageEndpoints
{
    public const string TokenHashSettingKey = "HomepageApiTokenHash";

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static void MapHomepageEndpoints(this WebApplication app)
    {
        var cacheRoot = Environment.GetEnvironmentVariable("CACHE_ROOT_PATH")
            ?? Path.Combine(app.Environment.ContentRootPath, "cache");

        app.MapGet("/api/homepage/stats", async (HttpContext http, CancellationToken ct) =>
        {
            var token = http.Request.Headers["X-API-Key"].ToString();
            if (string.IsNullOrWhiteSpace(token) || !Directory.Exists(cacheRoot))
                return Results.Unauthorized();

            var hash = Encoding.ASCII.GetBytes(HashToken(token.Trim()));

            // One DB per account that has ever signed in - a handful at most, so checking each
            // one per poll is cheaper than keeping a token index in sync.
            foreach (var dbPath in Directory.EnumerateFiles(cacheRoot, "meural-cache.db", SearchOption.AllDirectories))
            {
                var store = new PlaylistCacheStore(dbPath);
                var stored = await store.GetSettingAsync(TokenHashSettingKey, ct);
                if (stored is null || !CryptographicOperations.FixedTimeEquals(hash, Encoding.ASCII.GetBytes(stored)))
                    continue;

                var summary = await store.GetSummaryAsync(ct);
                return Results.Ok(new
                {
                    playlists = summary.Playlists,
                    images = summary.Images,
                    lastScan = summary.LastRefreshedUtc,
                });
            }

            return Results.Unauthorized();
        });
    }
}
