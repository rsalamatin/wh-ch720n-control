using HeadphoneControl.Core;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace HeadphoneControl.Platform.Windows;

/// <summary>Reports a paired Sony headset each time its Bluetooth link to this PC comes up.</summary>
/// <remarks>
/// Watches the paired devices' <c>IsConnected</c> property, which is the ACL link that audio uses too, so a headset
/// that Windows reconnects for audio counts. The control channel stays closed until someone connects it.
/// </remarks>
public sealed class BluetoothPresenceMonitor : IHeadsetPresenceMonitor
{
    private const string IsConnectedProperty = "System.Devices.Aep.IsConnected";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<BluetoothPresenceMonitor> _logger;
    private readonly HeadsetDiscovery _discovery;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();

    // Per device id, the last reported link state, so only a change from down to up is announced.
    private readonly Dictionary<string, PairedDevice> _devices = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private bool _disposed;

    public BluetoothPresenceMonitor(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<BluetoothPresenceMonitor>();
        _discovery = new HeadsetDiscovery(loggerFactory.CreateLogger<HeadsetDiscovery>());
    }

    public event EventHandler<DiscoveredHeadset>? HeadsetConnected;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher is not null)
            {
                throw new InvalidOperationException("The Bluetooth presence monitor is already started.");
            }

            try
            {
                var watcher = DeviceInformation.CreateWatcher(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    [IsConnectedProperty],
                    DeviceInformationKind.AssociationEndpoint);
                watcher.Added += OnAdded;
                watcher.Updated += OnUpdated;
                watcher.Removed += OnRemoved;
                watcher.Stopped += OnStopped;
                watcher.Start();
                _watcher = watcher;
            }
            catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
            {
                var error = TransportErrors.FromPlatformError("Watching paired Bluetooth devices", ex);
                _logger.LogError(ex, "{Message}", error.Message);
                throw error;
            }
        }
    }

    public void Dispose()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _devices.Clear();
        }

        // Not disposed: a probe that is still running may register on its token.
        _lifetime.Cancel();
        if (watcher is null)
        {
            return;
        }

        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.Stopped -= OnStopped;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception ex) when (TransportErrors.IsPlatformError(ex))
        {
            _logger.LogWarning(ex, "Stopping the Bluetooth device watcher failed");
        }
    }

    private static bool IsConnected(IReadOnlyDictionary<string, object> properties) =>
        properties.TryGetValue(IsConnectedProperty, out var value) && value is true;

    private void OnAdded(DeviceWatcher sender, DeviceInformation info) =>
        Track(info.Id, info.Name, IsConnected(info.Properties));

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        if (update.Properties.ContainsKey(IsConnectedProperty))
        {
            Track(update.Id, name: null, IsConnected(update.Properties));
        }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            _devices.Remove(update.Id);
        }
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        if (sender.Status == DeviceWatcherStatus.Aborted)
        {
            _logger.LogWarning("Windows stopped reporting Bluetooth connections; automatic connect is off until restart");
        }
    }

    private void Track(string deviceId, string? name, bool connected)
    {
        string displayName;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var known = _devices.GetValueOrDefault(deviceId);
            displayName = name ?? known?.Name ?? deviceId;
            _devices[deviceId] = new PairedDevice(displayName, connected);
            if (!connected || known is { IsConnected: true })
            {
                return;
            }
        }

        _ = AnnounceAsync(deviceId, displayName);
    }

    // Runs unobserved, so nothing may escape it.
    private async Task AnnounceAsync(string deviceId, string name)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(ProbeTimeout);
        DiscoveredHeadset? headset;
        try
        {
            headset = await _discovery.ProbeAsync(deviceId, name, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogWarning(ex, "Checking whether {Name} is a Sony headset took longer than {Timeout} s", name, ProbeTimeout.TotalSeconds);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking whether {Name} is a Sony headset failed", name);
            return;
        }

        // A probe that finished while the monitor was being disposed reports nothing.
        if (headset is null || _lifetime.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("{Name} connected to this PC over Bluetooth", headset.Name);
        try
        {
            HeadsetConnected?.Invoke(this, headset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A HeadsetConnected handler failed");
        }
    }

    private sealed record PairedDevice(string Name, bool IsConnected);
}
