namespace HeadphoneControl.Tests.ViewModels;

// Completed explicitly by the test, so no test waits on real time.
public sealed class ManualDelay
{
    private readonly List<TaskCompletionSource> _pending = [];

    public int PendingCount => _pending.Count;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource();
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        _pending.Add(completion);
        return completion.Task;
    }

    public void ElapseAll()
    {
        var pending = _pending.ToArray();
        _pending.Clear();
        foreach (var completion in pending)
        {
            completion.TrySetResult();
        }
    }
}
