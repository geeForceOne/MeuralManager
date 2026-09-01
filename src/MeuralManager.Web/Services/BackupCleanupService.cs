namespace MeuralManager.Web.Services;

// Periodically sweeps finished/abandoned backup zips - see BackupArchiveService.PurgeOlderThan.
public sealed class BackupCleanupService(BackupArchiveService archive) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            archive.PurgeOlderThan(MaxAge);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
