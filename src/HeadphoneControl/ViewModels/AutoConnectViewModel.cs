using HeadphoneControl.Core;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.ViewModels;

// Asks for a connect when a paired headset's Bluetooth link to this PC comes up (switched on, or reconnected for
// audio), so the controls are ready without pressing Connect. A control channel closed with Disconnect stays closed
// until the headset itself joins the PC again.
public sealed class AutoConnectViewModel : ViewModelBase, IDisposable
{
    // Gives the headset a moment to finish setting up audio before the control channel is opened next to it.
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2);

    // A connect attempt brings the Bluetooth link up by itself, so after a failure the headset is reported again;
    // without a pause a headset that refuses the control channel would be retried forever.
    internal static readonly TimeSpan FailureHoldOff = TimeSpan.FromMinutes(1);

    private readonly IHeadsetPresenceMonitor _monitor;
    private readonly Action<bool> _save;
    private readonly ILogger _logger;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();

    private bool _isEnabled;
    private bool _isWaiting;
    private DateTimeOffset _holdOffUntil = DateTimeOffset.MinValue;

    public AutoConnectViewModel(
        IHeadsetPresenceMonitor monitor,
        bool isEnabled,
        Action<bool> save,
        ILogger<AutoConnectViewModel> logger,
        // Runs an action on the UI thread; the monitor reports on any thread.
        Action<Action> dispatch,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(save);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(dispatch);

        _monitor = monitor;
        _isEnabled = isEnabled;
        _save = save;
        _logger = logger;
        _dispatch = dispatch;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _monitor.HeadsetConnected += OnHeadsetConnected;
    }

    // Raised on the UI thread; the receiver decides whether a connect can start now.
    public event EventHandler? ConnectRequested;

    // Applies from the next time the headset joins the PC.
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (value == _isEnabled)
            {
                return;
            }

            _isEnabled = value;
            _save(value);
            OnPropertyChanged();
        }
    }

    // Called when a connect, automatic or not, failed.
    public void HoldOff() => _holdOffUntil = _timeProvider.GetUtcNow() + FailureHoldOff;

    public void Dispose()
    {
        _monitor.HeadsetConnected -= OnHeadsetConnected;

        // Not disposed: a report already queued on the dispatcher may still read its token.
        _lifetime.Cancel();
    }

    private void OnHeadsetConnected(object? sender, DiscoveredHeadset headset) =>
        _dispatch(() => _ = ConnectAfterSettlingAsync(headset));

    private async Task ConnectAfterSettlingAsync(DiscoveredHeadset headset)
    {
        if (!IsEnabled || _isWaiting || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _isWaiting = true;
        try
        {
            _logger.LogDebug("{Name} joined this PC", headset.Name);
            await Task.Delay(SettleDelay, _timeProvider, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed while waiting; nobody is left to connect.
            return;
        }
        finally
        {
            _isWaiting = false;
        }

        // Checked after the delay: the failure of the attempt that raised this report may only just have happened.
        if (!IsEnabled)
        {
            return;
        }

        if (_timeProvider.GetUtcNow() < _holdOffUntil)
        {
            _logger.LogInformation("{Name} joined this PC; not connecting so soon after a failed connect", headset.Name);
            return;
        }

        ConnectRequested?.Invoke(this, EventArgs.Empty);
    }
}
