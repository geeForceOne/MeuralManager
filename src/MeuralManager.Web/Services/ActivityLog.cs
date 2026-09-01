namespace MeuralManager.Web.Services;

// Bounded, UI-bindable stand-in for the WinForms tabs' scrolling log TextBox: pages new one up
// per operation area and feed it via AsProgress(), same IProgress<string> shape CleanupService,
// BackupService, and PlaylistService already expect.
public sealed class ActivityLog
{
    private const int MaxEntries = 300;
    private readonly List<(DateTime When, string Message)> _entries = [];

    public event Action? Changed;

    public IReadOnlyList<(DateTime When, string Message)> Entries => _entries;

    public void Append(string message)
    {
        _entries.Add((DateTime.Now, message));
        if (_entries.Count > MaxEntries)
            _entries.RemoveRange(0, _entries.Count - MaxEntries);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _entries.Clear();
        Changed?.Invoke();
    }

    public IProgress<string> AsProgress() => new Progress<string>(Append);
}
