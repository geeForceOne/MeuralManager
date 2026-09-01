namespace MeuralManager.Core.Services;

// Disk store for "pre-crop" originals, so a crop can be non-destructively reverted. One file
// per current item id (named "{itemId}{ext}"), mirroring ImageCacheStore's layout and its
// file-existence-is-the-source-of-truth approach - no separate DB bookkeeping needed to know
// whether a given item has a revertible original.
public static class EditOriginalStore
{
    public static bool TryGetOriginalFile(string originalsDir, long itemId, out string path)
    {
        if (Directory.Exists(originalsDir))
        {
            var match = Directory.EnumerateFiles(originalsDir, $"{itemId}.*").FirstOrDefault();
            if (match is not null)
            {
                path = match;
                return true;
            }
        }

        path = "";
        return false;
    }

    // Copies sourceFilePath (the pre-crop bytes, wherever they currently live) into the
    // originals store keyed by itemId, preserving its extension.
    public static void SaveOriginal(string originalsDir, long itemId, string sourceFilePath)
    {
        Directory.CreateDirectory(originalsDir);
        var ext = Path.GetExtension(sourceFilePath);
        if (string.IsNullOrEmpty(ext))
            ext = ".jpg";
        var dest = Path.Combine(originalsDir, $"{itemId}{ext}");
        File.Copy(sourceFilePath, dest, overwrite: true);
    }

    // Re-keys an already-stored original from oldItemId to newItemId - used when cropping an
    // item that's already the result of an earlier crop, so the TRUE original carries forward
    // under the new item's id instead of being replaced by the (already-cropped) pre-crop
    // bytes. Returns false (no-op) if oldItemId had no stored original.
    public static bool TryRenameOriginal(string originalsDir, long oldItemId, long newItemId, out string path)
    {
        if (!TryGetOriginalFile(originalsDir, oldItemId, out var existing))
        {
            path = "";
            return false;
        }

        var ext = Path.GetExtension(existing);
        path = Path.Combine(originalsDir, $"{newItemId}{ext}");
        File.Move(existing, path, overwrite: true);
        return true;
    }

    // Called both after a successful revert (there's no more edit to undo) and whenever an item
    // is permanently removed (e.g. via Orphan Uploads cleanup), so a cropped item deleted
    // without ever being reverted doesn't leave its original orphaned on disk forever.
    public static void RemoveOriginal(string originalsDir, long itemId)
    {
        if (TryGetOriginalFile(originalsDir, itemId, out var path))
            File.Delete(path);
    }
}
