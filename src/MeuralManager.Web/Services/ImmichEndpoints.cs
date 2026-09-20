using MeuralManager.Core.Api;
using MeuralManager.Core.Models;

namespace MeuralManager.Web.Services;

public static class ImmichEndpoints
{
    public static void MapImmichEndpoints(this WebApplication app)
    {
        // A photo's thumbnail/preview/fullsize rendition, fetched from Immich with the API key
        // added server-side. Immich's own ids are GUIDs, so anything else is rejected outright
        // rather than being forwarded into a request path.
        app.MapGet("/immich-proxy/{token}/asset/{assetId}/{size}", async (
            string token, string assetId, string size, ImmichProxyRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            if (registry.Get(token) is not { } client)
                return Results.NotFound();
            if (!Guid.TryParse(assetId, out _) || size is not ("thumbnail" or "preview" or "fullsize"))
                return Results.BadRequest();

            return await StreamAsync(http, () => client.GetAssetThumbnailAsync(assetId, size, ct));
        });

        // The best Meural-ready version of a photo (the JPEG/PNG original, or Immich's converted
        // fullsize/preview JPEG for HEIC/RAW) - what the crop dialog loads, so it's cropping the
        // same bytes that would otherwise be uploaded as-is. The caller passes the original's mime
        // type because that's what decides "original or converted", and the search results it
        // came from already carry it.
        app.MapGet("/immich-proxy/{token}/image/{assetId}", async (
            string token, string assetId, string? mime, ImmichProxyRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            if (registry.Get(token) is not { } client)
                return Results.NotFound();
            if (!Guid.TryParse(assetId, out _))
                return Results.BadRequest();

            var asset = new ImmichAsset { Id = assetId, OriginalMimeType = mime };
            return await StreamAsync(http, () => client.GetBestImageAsync(asset, ct));
        });

        app.MapGet("/immich-proxy/{token}/person/{personId}", async (
            string token, string personId, ImmichProxyRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            if (registry.Get(token) is not { } client)
                return Results.NotFound();
            if (!Guid.TryParse(personId, out _))
                return Results.BadRequest();

            return await StreamAsync(http, () => client.GetPersonThumbnailAsync(personId, ct));
        });
    }

    private static async Task<IResult> StreamAsync(HttpContext http, Func<Task<HttpResponseMessage>> fetch)
    {
        HttpResponseMessage response;
        try
        {
            response = await fetch();
        }
        catch (ImmichApiException ex)
        {
            return Results.StatusCode((int)(ex.StatusCode ?? System.Net.HttpStatusCode.BadGateway));
        }

        // Disposed once the response has been fully written, not before - the body is streamed
        // straight through from Immich rather than buffered.
        http.Response.RegisterForDispose(response);
        http.Response.Headers.CacheControl = "private, max-age=86400";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        return Results.Stream(await response.Content.ReadAsStreamAsync(), contentType);
    }
}
