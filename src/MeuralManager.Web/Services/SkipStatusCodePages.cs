using Microsoft.AspNetCore.Http.Metadata;

namespace MeuralManager.Web.Services;

// Endpoint metadata that keeps an endpoint's own 401/404 instead of letting
// UseStatusCodePagesWithReExecute (Program.cs) swap in the NotFound page: the API, image, proxy and
// backup endpoints are fetched by code or <img> tags, which want a plain status code.
public sealed class SkipStatusCodePages : ISkipStatusCodePagesMetadata;
