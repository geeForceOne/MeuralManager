namespace MeuralManager.Web.Services;

// When the "What's new" banner shows (WhatsNewBanner): once per account after the app is updated,
// until it's closed. An account with no stored version (new, or the first run with the banner)
// starts at the running version, so nobody is told about a release they've been using all along.
// Local "dev" builds never announce anything.
public static class WhatsNew
{
    public enum Decision { Hide, Announce, StartAtRunning }

    public static Decision Decide(string running, string? lastSeen)
    {
        if (!Version.TryParse(running, out var runningVersion))
            return Decision.Hide;
        if (lastSeen is null)
            return Decision.StartAtRunning;
        return Version.TryParse(lastSeen, out var seenVersion) && seenVersion >= runningVersion
            ? Decision.Hide
            : Decision.Announce;
    }
}
