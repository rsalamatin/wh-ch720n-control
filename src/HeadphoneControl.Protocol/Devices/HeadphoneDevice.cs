using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// The real headset: opens a <see cref="SonyV2Connection"/> on every connect and keeps the <see cref="DeviceState"/>.
/// No V2 command is sent until both the transport's service and the init handshake confirm a V2 device.
/// </summary>
public sealed class HeadphoneDevice : IHeadphoneDevice
{
    private readonly Func<CancellationToken, Task<TransportConnection>> _connect;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HeadphoneDevice> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _stateLock = new();

    private DeviceState _state = DeviceState.Disconnected;

    // Written only under _operationLock. The connection event handlers read it without the lock, which is safe
    // because SonyV2Connection.DisposeAsync waits for its last event before a released connection is replaced.
    private SonyV2Connection? _connection;
    private bool _disposed;

    /// <param name="connect">
    /// Opens the transport and reports which Sony service it connected to. Called on every
    /// <see cref="ConnectAsync"/>.
    /// </param>
    public HeadphoneDevice(
        string name,
        Func<CancellationToken, Task<TransportConnection>> connect,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connect);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Name = name;
        _connect = connect;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HeadphoneDevice>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name { get; }

    public DeviceState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken) =>
        RunAsync(
            async ct =>
            {
                if (_connection is not null && State.Connection == ConnectionStatus.Connected)
                {
                    return;
                }

                await ReleaseConnectionAsync().ConfigureAwait(false);
                Publish(_ => DeviceState.Disconnected with { Connection = ConnectionStatus.Connecting });
                try
                {
                    var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
                    var settings = await connection.ReadAllAsync(ct).ConfigureAwait(false);

                    // Connected and the settings are published together, so the UI never sees "connected, all unknown".
                    // The connection latches IsLinkLost before raising LinkLost, whose handler publishes under the same
                    // lock: a link that drops after the last reply either stops this publish or finds Connected and
                    // clears it.
                    var lost = false;
                    Publish(current =>
                    {
                        if (connection.IsLinkLost)
                        {
                            lost = true;
                            return current;
                        }

                        return settings.ApplyTo(current) with
                        {
                            Connection = ConnectionStatus.Connected,
                            Generation = ProtocolGeneration.V2,
                        };
                    });
                    if (lost)
                    {
                        throw new TransportException($"The link to {Name} dropped while connecting.");
                    }
                }
                catch (Exception ex)
                {
                    var cancelled = ex is OperationCanceledException && ct.IsCancellationRequested;
                    if (!cancelled)
                    {
                        _logger.LogError(ex, "Connecting to {Name} failed", Name);
                    }

                    await ReleaseConnectionAsync().ConfigureAwait(false);
                    Publish(_ => cancelled
                        ? DeviceState.Disconnected
                        : DeviceState.Disconnected with { Connection = ConnectionStatus.Failed });
                    throw;
                }
            },
            cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunAsync(
            async _ =>
            {
                await ReleaseConnectionAsync().ConfigureAwait(false);
                Publish(_ => DeviceState.Disconnected);
            },
            cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (connection, ct) =>
            {
                var settings = await connection.ReadAllAsync(ct).ConfigureAwait(false);
                PublishIfConnected(settings.ApplyTo);
            },
            cancellationToken);

    public Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendSettingAsync(
            (connection, ct) => connection.SetNoiseControlAsync(state, ct),
            current => current with { NoiseControl = NormalizeNoiseControl(state) },
            cancellationToken);
    }

    public Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (connection, ct) =>
            {
                await connection.SetEqualizerPresetAsync(preset, ct).ConfigureAwait(false);

                // Captured after the ACK: anything applied from here on describes the device after the SET.
                var previous = State.Equalizer;

                // The device owns each preset's band curve, so it is re-read. Once the SET is ACKed the caller's
                // cancellation no longer applies: abandoning the re-read would leave the old preset's bands showing.
                var equalizer = await connection.TryReadEqualizerAsync(_lifetime.Token).ConfigureAwait(false);
                if (equalizer is not null)
                {
                    PublishIfConnected(current => current with { Equalizer = equalizer });
                    return;
                }

                // Keeping the previous preset's bands would let a later band edit send a curve the user never saw.
                // A late reply or notification after the timeout installs a new instance, which is kept.
                _logger.LogWarning("Equalizer of {Name} is unknown after switching to {Preset}", Name, preset);
                PublishIfConnected(current => ReferenceEquals(current.Equalizer, previous)
                    ? current with { Equalizer = null }
                    : current);
            },
            cancellationToken);

    public Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var snapshot = bands.ToArray();
        return SendSettingAsync(
            (connection, ct) => connection.SetCustomEqualizerAsync(clearBass, snapshot, ct),
            current => current with { Equalizer = new EqualizerState(EqualizerPreset.Manual, clearBass, snapshot) },
            cancellationToken);
    }

    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendSettingAsync(
            (connection, ct) => connection.SetDseeAsync(enabled, ct),
            current => current with { DseeEnabled = enabled },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // Cancelled first so a running connect (tens of seconds against an absent headset) releases the lock promptly.
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _operationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await ReleaseConnectionAsync().ConfigureAwait(false);
            Publish(_ => DeviceState.Disconnected);
        }
        finally
        {
            _operationLock.Release();
        }

        // _lifetime and _operationLock are deliberately not disposed: a caller that lost the race may still be
        // releasing them, and neither owns a wait handle or timer that would leak.
    }

    // Mirrors what the device stores: the wire level is max(1, level) and 0 means "not used".
    private static NoiseControlState NormalizeNoiseControl(NoiseControlState state) =>
        state with { AmbientLevel = Math.Max(1, state.AmbientLevel) };

    private static bool IsContractFailure(Exception ex) =>
        ex is IOException or TimeoutException or NotSupportedException or FormatException
            or InvalidOperationException or ArgumentException;

    // Caller holds _operationLock.
    private async Task<SonyV2Connection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var transport = await _connect(cancellationToken).ConfigureAwait(false);
        var connection = await SonyV2Connection.OpenAsync(transport, _loggerFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        _connection = connection;
        connection.NotificationReceived += OnNotificationReceived;
        connection.LinkLost += OnLinkLost;
        _logger.LogInformation("{Name} confirmed as a V2 device (service and handshake)", Name);
        return connection;
    }

    private Task SendSettingAsync(
        Func<SonyV2Connection, CancellationToken, Task> send,
        Func<DeviceState, DeviceState> apply,
        CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (connection, ct) =>
            {
                await send(connection, ct).ConfigureAwait(false);
                PublishIfConnected(apply);
            },
            cancellationToken);

    private Task RunConnectedAsync(
        Func<SonyV2Connection, CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        RunAsync(
            ct =>
            {
                if (_connection is not { } connection || State.Connection != ConnectionStatus.Connected)
                {
                    throw new InvalidOperationException($"{Name} is not connected.");
                }

                return operation(connection, ct);
            },
            cancellationToken);

    // Serializes operations, links them to the device lifetime, and enforces the IHeadphoneDevice exception
    // contract: platform code below (WinRT via the connector) can throw types the UI does not expect.
    private async Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _operationLock.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await operation(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller's cancellation: either this device is being disposed or the platform aborted the I/O.
            throw _lifetime.IsCancellationRequested
                ? new ObjectDisposedException(GetType().Name)
                : new TransportException("The Bluetooth operation was aborted.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !IsContractFailure(ex))
        {
            throw new TransportException($"Unexpected failure talking to {Name}: {ex.Message}", ex);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private void OnNotificationReceived(object? sender, ReadOnlyMemory<byte> payload)
    {
        if (sender is not SonyV2Connection connection || !ReferenceEquals(connection, _connection))
        {
            return;
        }

        try
        {
            DeviceState? updated = null;
            bool recognised;
            lock (_stateLock)
            {
                recognised = connection.TryApplyNotification(_state, payload.Span, out var next);
                if (recognised && next != _state)
                {
                    _state = next;
                    updated = next;
                }
            }

            if (updated is not null)
            {
                StateChanged?.Invoke(this, updated);
            }
            else
            {
                // The headset echoes every SET as a notification, which usually matches the state the ACK set.
                _logger.LogDebug(
                    recognised ? "Notification {Payload} confirms the current state" : "Ignored notification {Payload}",
                    Convert.ToHexString(payload.Span));
            }
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Malformed notification {Payload}", Convert.ToHexString(payload.Span));
        }
    }

    private void OnLinkLost(object? sender, Exception? cause)
    {
        if (!ReferenceEquals(sender, _connection))
        {
            return;
        }

        _logger.LogWarning(cause, "Link to {Name} lost", Name);

        // While connecting, ConnectAsync sees IsLinkLost and reports Failed itself.
        PublishIfConnected(_ => DeviceState.Disconnected);
    }

    // Caller holds _operationLock.
    private async Task ReleaseConnectionAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is null)
        {
            return;
        }

        connection.NotificationReceived -= OnNotificationReceived;
        connection.LinkLost -= OnLinkLost;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    // A reply or ACK that completes after the link dropped must not resurrect a disconnected state.
    private void PublishIfConnected(Func<DeviceState, DeviceState> change) =>
        Publish(current => current.Connection == ConnectionStatus.Connected ? change(current) : current);

    private void Publish(Func<DeviceState, DeviceState> change)
    {
        DeviceState updated;
        lock (_stateLock)
        {
            updated = change(_state);
            if (updated == _state)
            {
                return;
            }

            _state = updated;
        }

        StateChanged?.Invoke(this, updated);
    }
}
