using MeuralManager.Core.Services;

namespace MeuralManager.Web.Services;

public static class ImageCacheEndpoints
{
    public static void MapImageCacheEndpoints(this WebApplication app)
    {
        // Serves one item's image from the local cache, downloading and caching it first on a
        // miss - see ImageCacheManager.GetOrDownloadImageAsync. {email} identifies which
        // account's cache to use, since more than one account can be signed in against the same
        // running app instance.
        app.MapGet("/image-cache/{email}/{itemId:long}", async (string email, long itemId, ImageCacheManager cache, HttpContext http, CancellationToken ct) =>
        {
            var path = await cache.GetOrDownloadImageAsync(email, itemId, ct);
            if (path is null)
                return Results.NotFound();

            // Cached content is stable once downloaded (an item's image doesn't change under a
            // given id), so let the browser skip re-requesting it entirely on repeat visits -
            // this is most of what makes the app feel faster.
            http.Response.Headers.CacheControl = "private, max-age=604800, immutable";
            return Results.File(path, FileNaming.GuessContentType(path));
        });
    }
}
