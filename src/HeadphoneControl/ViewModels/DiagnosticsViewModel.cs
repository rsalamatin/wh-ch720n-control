using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeadphoneControl.Diagnostics;

namespace HeadphoneControl.ViewModels;

public sealed partial class DiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly DiagnosticsJournal _journal;
    private readonly Action<Action> _dispatch;
    private int _refreshQueued;

    public DiagnosticsViewModel(DiagnosticsJournal journal, Action<Action> dispatch)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(dispatch);
        _journal = journal;
        _dispatch = dispatch;
        Text = BuildText();
        _journal.Changed += OnJournalChanged;
    }

    [ObservableProperty]
    public partial string Text { get; private set; }

    public void Dispose() => _journal.Changed -= OnJournalChanged;

    [RelayCommand]
    private void Clear() => _journal.Clear();

    private void OnJournalChanged(object? sender, EventArgs e)
    {
        // Bursts of log lines collapse into a single UI update.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _dispatch(() =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Text = BuildText();
        });
    }

    private string BuildText() => string.Join(Environment.NewLine, _journal.Snapshot());
}
