namespace MeuralManager.Web.Services;

// A photo library this app can browse and upload from. Both speak Immich's REST API (the backup
// app answers the part of it ImmichApiClient uses), so they share the client, the proxy, the
// settings plumbing and one page component (PhotoSourceBrowser); this record holds what differs.
//
// Key prefixes the per-account settings rows: "<Key>ServerUrl", "<Key>ApiKey", "<Key>ProxyToken",
// "<Key>UploadedAssetIds" - for Immich these are the names it has always used.
public sealed record PhotoSource(
    string Key,
    string Name,
    string Route,
    string Subtitle,
    string AlbumsLabel,
    string AlbumWord,
    bool HasPeople,
    bool CanRestoreAlbums,
    string SearchHint,
    string SearchPlaceholder,
    string KeyLabel)
{
    public static readonly PhotoSource Immich = new(
        Key: "Immich",
        Name: "Immich",
        Route: "/immich",
        Subtitle: "Browse your Immich photos and send them to a Meural playlist.",
        AlbumsLabel: "Albums",
        AlbumWord: "album",
        HasPeople: true,
        CanRestoreAlbums: false,
        SearchHint: "(describe what's in the photo)",
        SearchPlaceholder: "e.g. beach at sunset",
        KeyLabel: "API key");

    // MeuralImageBackup (github.com/geeForceOne/MeuralImageBackup): keeps a copy of every image
    // shown on the frames, a folder per gallery. Its albums are those galleries, it has no people,
    // and its "smart" search is a plain search on image and gallery names.
    public static readonly PhotoSource ImageBackup = new(
        Key: "ImageBackup",
        Name: "Image Backup",
        Route: "/image-backup",
        Subtitle: "Browse what MeuralImageBackup saved from your frames and send it back to a Meural playlist.",
        AlbumsLabel: "Galleries",
        AlbumWord: "gallery",
        HasPeople: false,
        CanRestoreAlbums: true,
        SearchHint: "(image or gallery name)",
        SearchPlaceholder: "e.g. water lilies",
        KeyLabel: "API token");

    public static IReadOnlyList<PhotoSource> All { get; } = [Immich, ImageBackup];

    public bool IsEnabled(UserPreferencesStore.StoredPreferences prefs) =>
        this == ImageBackup ? prefs.ImageBackupEnabled : prefs.ImmichEnabled;

    // The backup names a gallery "<name> · <frame>" when it has several frames, with the frame in
    // the description; a restored playlist gets just the gallery's own name.
    public static string PlaylistNameFor(string? albumName, string? description)
    {
        var name = albumName?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(description))
        {
            var suffix = $" · {description.Trim()}";
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                name = name[..^suffix.Length];
        }

        return name.Length > 0 ? name : "Restored gallery";
    }
}
