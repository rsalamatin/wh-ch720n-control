using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// The real headset: composes a transport, a <see cref="ProtocolSession"/> and the <see cref="V2CommandSet"/>.
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

    // Written only under _operationLock. The session event handlers read them without it, which is safe because
    // ProtocolSession.DisposeAsync waits for its dispatch task before a released session's fields are replaced.
    private ProtocolSession? _session;
    private V2CommandSet? _commands;
    // Guarded by _stateLock, so a link loss is atomic with ConnectAsync's final publish.
    private bool _linkLost;
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
                if (_session is not null && State.Connection == ConnectionStatus.Connected)
                {
                    return;
                }

                await ReleaseSessionAsync().ConfigureAwait(false);
                Publish(_ => DeviceState.Disconnected with { Connection = ConnectionStatus.Connecting });
                try
                {
                    await OpenSessionAsync(ct).ConfigureAwait(false);
                    var read = await ReadAllAsync(ct).ConfigureAwait(false);

                    // Connected and the settings are published together, so the UI never sees "connected, all unknown".
                    // A link that drops after the last reply either stops this publish or finds Connected and clears it.
                    var lost = false;
                    Publish(current =>
                    {
                        if (_linkLost)
                        {
                            lost = true;
                            return current;
                        }

                        return read(current) with
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

                    await ReleaseSessionAsync().ConfigureAwait(false);
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
                await ReleaseSessionAsync().ConfigureAwait(false);
                Publish(_ => DeviceState.Disconnected);
            },
            cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (_, _, ct) =>
            {
                var read = await ReadAllAsync(ct).ConfigureAwait(false);
                PublishIfConnected(read);
            },
            cancellationToken);

    public Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendSettingAsync(
            commands => commands.SetNoiseControl(state),
            current => current with { NoiseControl = NormalizeNoiseControl(state) },
            cancellationToken);
    }

    public Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (session, commands, ct) =>
            {
                await session.SendAsync(commands.SetEqualizerPreset(preset).Payload, ct).ConfigureAwait(false);

                // Captured after the ACK: anything applied from here on describes the device after the SET.
                var previous = State.Equalizer;

                // The device owns each preset's band curve, so it is re-read. Once the SET is ACKed the caller's
                // cancellation no longer applies: abandoning the re-read would leave the old preset's bands showing.
                var equalizer = await TryQueryAsync(
                        session, commands.QueryEqualizer(), commands.ParseEqualizer, _lifetime.Token)
                    .ConfigureAwait(false);
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
            commands => commands.SetEqualizerCustom(clearBass, snapshot),
            current => current with { Equalizer = new EqualizerState(EqualizerPreset.Manual, clearBass, snapshot) },
            cancellationToken);
    }

    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendSettingAsync(
            commands => commands.SetDsee(enabled),
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
            await ReleaseSessionAsync().ConfigureAwait(false);
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

    private async Task OpenSessionAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _linkLost = false;
        }

        var connection = await _connect(cancellationToken).ConfigureAwait(false);
        var session = new ProtocolSession(
            connection.Transport, _loggerFactory.CreateLogger<ProtocolSession>(), _timeProvider);
        _session = session;
        session.NotificationReceived += OnNotificationReceived;
        session.Disconnected += OnSessionDisconnected;
        await session.StartAsync(cancellationToken).ConfigureAwait(false);

        // The init opcode 0x00 is harmless on every generation, so it is the only thing sent before the check.
        var handshake = ProtocolHandshake.CreateRequest();
        var reply = await session.RequestAsync(
            handshake.Payload, handshake.ResponseOpcode!.Value, handshake.ResponseSubtype, cancellationToken)
            .ConfigureAwait(false);

        _commands = V2CommandSet.FromHandshake(connection.ServiceGeneration, reply.Span);
        _logger.LogInformation("{Name} confirmed as a V2 device (service and handshake)", Name);
    }

    private async Task<Func<DeviceState, DeviceState>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var session = _session!;
        var commands = _commands!;

        var battery = await TryQueryAsync(session, commands.QueryBattery(), commands.ParseBattery, cancellationToken)
            .ConfigureAwait(false);
        var noise = await TryQueryAsync(session, commands.QueryNoiseControl(), commands.ParseNoiseControl, cancellationToken)
            .ConfigureAwait(false);
        var equalizer = await TryQueryAsync(session, commands.QueryEqualizer(), commands.ParseEqualizer, cancellationToken)
            .ConfigureAwait(false);
        var dsee = await TryQueryValueAsync(session, commands.QueryDsee(), commands.ParseDsee, cancellationToken)
            .ConfigureAwait(false);
        var firmware = await TryQueryAsync(session, commands.QueryFirmwareVersion(), commands.ParseFirmwareVersion, cancellationToken)
            .ConfigureAwait(false);
        var codec = await TryQueryValueAsync(session, commands.QueryCodec(), commands.ParseCodec, cancellationToken)
            .ConfigureAwait(false);

        // Unanswered queries keep what the state already has, e.g. a value a notification delivered meanwhile.
        return current => current with
        {
            Battery = battery ?? current.Battery,
            NoiseControl = noise ?? current.NoiseControl,
            Equalizer = equalizer ?? current.Equalizer,
            DseeEnabled = dsee ?? current.DseeEnabled,
            FirmwareVersion = firmware ?? current.FirmwareVersion,
            Codec = codec ?? current.Codec,
        };
    }

    private async Task<T?> TryQueryAsync<T>(
        ProtocolSession session, MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
        where T : class =>
        await TryQueryCoreAsync(session, request, parse, cancellationToken).ConfigureAwait(false) is (true, var value)
            ? value
            : null;

    private async Task<T?> TryQueryValueAsync<T>(
        ProtocolSession session, MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
        where T : struct =>
        await TryQueryCoreAsync(session, request, parse, cancellationToken).ConfigureAwait(false) is (true, var value)
            ? value
            : null;

    // A single unanswered or malformed query must not fail the whole connection: the feature stays unknown
    // (null) and the UI disables it. Link loss still propagates.
    private async Task<(bool Found, T Value)> TryQueryCoreAsync<T>(
        ProtocolSession session, MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await session.RequestAsync(
                request.Payload, request.ResponseOpcode!.Value, request.ResponseSubtype, cancellationToken)
                .ConfigureAwait(false);
            return (true, parse(reply.Span));
        }
        catch (Exception ex) when (ex is TimeoutException or FormatException)
        {
            _logger.LogWarning(ex, "Query {Payload} failed; the setting stays unknown", Convert.ToHexString(request.Payload.Span));
            return (false, default!);
        }
    }

    private Task SendSettingAsync(
        Func<V2CommandSet, MdrRequest> build,
        Func<DeviceState, DeviceState> apply,
        CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (session, commands, ct) =>
            {
                await session.SendAsync(build(commands).Payload, ct).ConfigureAwait(false);
                PublishIfConnected(apply);
            },
            cancellationToken);

    private Task RunConnectedAsync(
        Func<ProtocolSession, V2CommandSet, CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        RunAsync(
            ct =>
            {
                if (_session is not { } session || _commands is not { } commands
                    || State.Connection != ConnectionStatus.Connected)
                {
                    throw new InvalidOperationException($"{Name} is not connected.");
                }

                return operation(session, commands, ct);
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

    private void OnNotificationReceived(object? sender, Frame frame)
    {
        var commands = _commands;
        if (commands is null || !ReferenceEquals(sender, _session))
        {
            return;
        }

        try
        {
            DeviceState? updated = null;
            bool recognised;
            lock (_stateLock)
            {
                recognised = commands.TryApplyNotification(_state, frame.Payload.Span, out var next);
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
                    Convert.ToHexString(frame.Payload.Span));
            }
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Malformed notification {Payload}", Convert.ToHexString(frame.Payload.Span));
        }
    }

    private void OnSessionDisconnected(object? sender, Exception? cause)
    {
        if (!ReferenceEquals(sender, _session))
        {
            return;
        }

        _logger.LogWarning(cause, "Link to {Name} lost", Name);

        // While connecting, ConnectAsync sees _linkLost and reports Failed itself.
        Publish(current =>
        {
            _linkLost = true;
            return current.Connection == ConnectionStatus.Connected ? DeviceState.Disconnected : current;
        });
    }

    // Caller holds _operationLock.
    private async Task ReleaseSessionAsync()
    {
        var session = _session;
        _session = null;
        _commands = null;
        if (session is null)
        {
            return;
        }

        session.NotificationReceived -= OnNotificationReceived;
        session.Disconnected -= OnSessionDisconnected;
        await session.DisposeAsync().ConfigureAwait(false);
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

    private delegate T SpanParser<out T>(ReadOnlySpan<byte> payload);
}
