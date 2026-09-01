namespace MeuralManager.Web.Services;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this WebApplication app)
    {
        app.MapGet("/backups/{id}/download", (string id, BackupArchiveService archive) =>
            archive.TryGetDownload(id, out var zipPath, out var fileName)
                ? Results.File(zipPath, "application/zip", fileName)
                : Results.NotFound());
    }
}
