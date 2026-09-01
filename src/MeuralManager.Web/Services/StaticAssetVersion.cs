namespace MeuralManager.Web.Services;

// Cache-busts the wwwroot assets referenced directly in App.razor (files not covered by
// Blazor's own fingerprinted framework bundle) by appending a "?v=" derived from each file's
// last-write time, so a new deployment invalidates any copy a browser cached under the old
// URL without needing a manual hard-refresh. In Development, Program.cs additionally sends
// no-cache headers for static files, so a version bump isn't relied on there.
public sealed class StaticAssetVersion(IWebHostEnvironment env)
{
    private readonly Dictionary<string, string> _cache = [];
    private readonly object _gate = new();

    public string Get(string wwwrootRelativePath)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(wwwrootRelativePath, out var cached))
                return cached;

            var fullPath = Path.Combine(env.WebRootPath, wwwrootRelativePath.Replace('/', Path.DirectorySeparatorChar));
            var version = File.Exists(fullPath)
                ? File.GetLastWriteTimeUtc(fullPath).Ticks.ToString()
                : "0";

            _cache[wwwrootRelativePath] = version;
            return version;
        }
    }
}
