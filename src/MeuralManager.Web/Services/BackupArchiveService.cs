using System.Collections.Concurrent;
using System.IO.Compression;

namespace MeuralManager.Web.Services;

// The web equivalent of the WinForms "choose a backup folder on this PC" flow: there's no
// local disk that means anything to a remote browser, so instead each backup operation writes
// into a private temp working directory, gets zipped into one file, and is handed back to the
// browser as a download. Registered as a singleton so the minimal API download endpoint (which
// runs in its own per-HTTP-request DI scope, separate from the Blazor circuit that started the
// backup) can still find the finished zip by its id.
public sealed class BackupArchiveService
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meuralmanager-web-backups");
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private sealed record Entry(string ZipPath, string FileName, DateTime CreatedUtc);

    // Creates a fresh empty directory for one backup operation to write into, identified by the
    // returned id (which will also become the download URL's id after FinishAsZip).
    public string BeginWorkingDirectory(out string id)
    {
        Directory.CreateDirectory(_root);
        id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Zips workingDir's contents, removes the now-redundant unzipped copy, and registers the
    // result for download at /backups/{id}/download.
    public void FinishAsZip(string id, string workingDir, string downloadFileName)
    {
        var zipPath = Path.Combine(_root, $"{id}.zip");
        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(workingDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        Directory.Delete(workingDir, recursive: true);

        _entries[id] = new Entry(zipPath, downloadFileName, DateTime.UtcNow);
    }

    public bool TryGetDownload(string id, out string zipPath, out string fileName)
    {
        if (_entries.TryGetValue(id, out var entry) && File.Exists(entry.ZipPath))
        {
            zipPath = entry.ZipPath;
            fileName = entry.FileName;
            return true;
        }

        zipPath = "";
        fileName = "";
        return false;
    }

    // Run periodically by BackupCleanupService - a Docker container is long-lived, so nothing
    // else would ever remove finished zips (once downloaded, the browser has its own copy) or
    // working directories left behind by an operation that was cancelled or crashed mid-backup.
    public void PurgeOlderThan(TimeSpan maxAge)
    {
        var cutoffUtc = DateTime.UtcNow - maxAge;

        foreach (var (id, entry) in _entries)
        {
            if (entry.CreatedUtc >= cutoffUtc)
                continue;

            _entries.TryRemove(id, out _);
            TryDeleteFile(entry.ZipPath);
        }

        if (!Directory.Exists(_root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoffUtc)
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Still being written to, or already gone - leave it for the next sweep.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
