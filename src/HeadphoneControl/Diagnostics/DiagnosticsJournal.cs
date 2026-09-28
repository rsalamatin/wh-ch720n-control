namespace HeadphoneControl.Diagnostics;

// Bounded so a long session can't grow memory without limit; the oldest lines are dropped first.
public sealed class DiagnosticsJournal
{
    private readonly Lock _gate = new();
    private readonly Queue<string> _lines;

    public DiagnosticsJournal(int capacity = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        _lines = new Queue<string>(capacity);
    }

    public int Capacity { get; }

    // Raised on the thread that changed the journal.
    public event EventHandler? Changed;

    public void Append(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lock (_gate)
        {
            if (_lines.Count == Capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }
}
