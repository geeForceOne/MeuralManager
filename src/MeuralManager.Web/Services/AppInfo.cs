using System.Reflection;

namespace MeuralManager.Web.Services;

// The running version: the release number the Docker build stamps in from the git tag
// (-p:InformationalVersion=1.2.0), or "dev" for a local build (see the .csproj).
public static class AppInfo
{
    public static string Version { get; } = ReadVersion();

    public static bool IsDevBuild => !char.IsDigit(Version[0]);

    // "v1.2.0" for a release, plain "dev" / "dev-abc1234" otherwise.
    public static string DisplayVersion => IsDevBuild ? Version : "v" + Version;

    private static string ReadVersion()
    {
        var info = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : info.Split('+')[0];
    }
}
