using System.Threading.Channels;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Core;

/// <summary>
/// The real headset behind <see cref="IHeadphoneDevice"/>: owns the connection lifecycle and opens a
/// <see cref="SonyV2Connection"/> on every connect. No V2 command is sent until both the transport's service and the
/// init handshake confirm a V2 device.
/// </summary>
/// <remarks>
/// One actor processes the user's operations and the link's events (notifications, link loss) in arrival order, so
/// nothing that changes the state runs concurrently. An event that arrives while an operation runs is applied after
/// that operation, and only to the settings for which it is the most recently received frame.
/// </remarks>
public sealed class HeadsetController : IHeadphoneDevice
{
    private readonly Func<CancellationToken, Task<TransportConnection>> _connect;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HeadsetController> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<Message> _mailbox =
        Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _actor;

    // Per Setting, the receive ordinal of the frame (reply, ACK or notification) that last set it on the current link.
    private readonly long[] _setAt = new long[Enum.GetValues<Setting>().Length];

    // Only the actor touches _link and _setAt and writes _state; State reads _state from any thread.
    private LinkState _link = new Disconnected();
    private volatile DeviceState _state = DeviceState.Disconnected;
    private int _disposeStarted;

    /// <param name="connect">
    /// Opens the transport and reports which Sony service it connected to. Called on every
    /// <see cref="ConnectAsync"/>.
    /// </param>
    public HeadsetController(
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
        _logger = loggerFactory.CreateLogger<HeadsetController>();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _actor = Task.Run(RunActorAsync);
    }

    public string Name { get; }

    public DeviceState State => _state;

    public event EventHandler<DeviceState>? StateChanged;

    public Task ConnectAsync(CancellationToken cancellationToken) => RunAsync(ConnectCoreAsync, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken) =>
        RunAsync(
            async _ =>
            {
                await ReleaseLinkAsync().ConfigureAwait(false);
                Publish(DeviceState.Disconnected);
            },
            cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                var settings = await link.ReadAllAsync(ct).ConfigureAwait(false);
                Publish(Merge(_state, settings));
            },
            cancellationToken);

    public Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendSettingAsync(
            (link, ct) => link.SetNoiseControlAsync(state, ct),
            Setting.NoiseControl,
            current => current with { NoiseControl = NormalizeNoiseControl(state) },
            cancellationToken);
    }

    public Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                var acknowledged = await link.SetEqualizerPresetAsync(preset, ct).ConfigureAwait(false);

                // The device owns each preset's band curve, so it is re-read. Once the SET is ACKed the caller's
                // cancellation no longer applies: abandoning the re-read would leave the old preset's bands showing.
                var equalizer = await link.TryReadEqualizerAsync(_lifetime.Token).ConfigureAwait(false);
                if (equalizer is null)
                {
                    _logger.LogWarning("Equalizer of {Name} is unknown after switching to {Preset}", Name, preset);
                }

                // Unknown rather than the previous preset's bands, which would let a later band edit send a curve the
                // user never saw. Stamped with the ACK, so a late curve received after it still wins and an older
                // one, e.g. the late reply to an earlier preset's re-read, does not.
                if (Claim(Setting.Equalizer, equalizer?.Ordinal ?? acknowledged))
                {
                    Publish(_state with { Equalizer = equalizer?.Value });
                }
            },
            cancellationToken);

    public Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var snapshot = bands.ToArray();
        return SendSettingAsync(
            (link, ct) => link.SetCustomEqualizerAsync(clearBass, snapshot, ct),
            Setting.Equalizer,
            current => current with { Equalizer = new EqualizerState(EqualizerPreset.Manual, clearBass, snapshot) },
            cancellationToken);
    }

    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendSettingAsync(
            (link, ct) => link.SetDseeAsync(enabled, ct),
            Setting.Dsee,
            current => current with { DseeEnabled = enabled },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var first = Interlocked.Exchange(ref _disposeStarted, 1) == 0;
        if (first)
        {
            // Cancelled first so a running connect (tens of seconds against an absent headset) stops promptly.
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _mailbox.Writer.TryComplete();
        }

        await _actor.ConfigureAwait(false);
        if (first)
        {
            _lifetime.Dispose();
        }
    }

    // Mirrors what the device stores: the wire level is max(1, level) and 0 means "not used".
    private static NoiseControlState NormalizeNoiseControl(NoiseControlState state) =>
        state with { AmbientLevel = Math.Max(1, state.AmbientLevel) };

    private static bool IsContractFailure(Exception ex) =>
        ex is IOException or TimeoutException or NotSupportedException or FormatException
            or InvalidOperationException or ArgumentException;

    private static bool Differs<T>(T current, T next) => !EqualityComparer<T>.Default.Equals(current, next);

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_link is Connected)
        {
            return;
        }

        _link = new Connecting();
        Publish(DeviceState.Disconnected with { Connection = ConnectionStatus.Connecting });
        SonyV2Connection? link = null;
        try
        {
            link = await OpenLinkAsync(cancellationToken).ConfigureAwait(false);

            // Ordinals restart with every session.
            Array.Clear(_setAt);
            var settings = await link.ReadAllAsync(cancellationToken).ConfigureAwait(false);

            // The link's LinkDropped message waits behind this operation, so a drop after the last reply is caught
            // here, and a later one finds Connected and clears it.
            if (link.IsLinkLost)
            {
                throw new TransportException($"The link to {Name} dropped while connecting.");
            }

            // Connected and the settings are published together, so the UI never sees "connected, all unknown".
            _link = new Connected(link);
            Publish(Merge(_state, settings) with
            {
                Connection = ConnectionStatus.Connected,
                Generation = ProtocolGeneration.V2,
            });
        }
        catch (Exception ex)
        {
            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled)
            {
                _logger.LogError(ex, "Connecting to {Name} failed", Name);
            }

            if (link is not null)
            {
                await CloseAsync(link).ConfigureAwait(false);
            }

            _link = cancelled ? new Disconnected() : new Failed();
            Publish(cancelled
                ? DeviceState.Disconnected
                : DeviceState.Disconnected with { Connection = ConnectionStatus.Failed });
            throw;
        }
    }

    private async Task<SonyV2Connection> OpenLinkAsync(CancellationToken cancellationToken)
    {
        var transport = await _connect(cancellationToken).ConfigureAwait(false);
        var link = await SonyV2Connection.OpenAsync(transport, _loggerFactory, _timeProvider, cancellationToken)
            .ConfigureAwait(false);
        link.NotificationReceived += OnNotificationReceived;
        link.LinkLost += OnLinkLost;
        _logger.LogInformation("{Name} confirmed as a V2 device (service and handshake)", Name);
        return link;
    }

    // Records that the frame with this ordinal set the setting, unless a later frame already did. Operations and
    // queued notifications reach the actor out of receive order (a notification received before an operation's ACK
    // is applied after that operation), so an older frame must not overwrite a newer one.
    private bool Claim(Setting setting, long ordinal)
    {
        if (ordinal <= _setAt[(int)setting])
        {
            return false;
        }

        _setAt[(int)setting] = ordinal;
        return true;
    }

    // An unanswered setting keeps what the state already has, e.g. a value a newer notification delivered.
    private DeviceState Merge(DeviceState current, DeviceSettings settings) => current with
    {
        Battery = settings.Battery is { } battery && Claim(Setting.Battery, battery.Ordinal)
            ? battery.Value
            : current.Battery,
        NoiseControl = settings.NoiseControl is { } noise && Claim(Setting.NoiseControl, noise.Ordinal)
            ? noise.Value
            : current.NoiseControl,
        Equalizer = settings.Equalizer is { } equalizer && Claim(Setting.Equalizer, equalizer.Ordinal)
            ? equalizer.Value
            : current.Equalizer,
        DseeEnabled = settings.DseeEnabled is { } dsee && Claim(Setting.Dsee, dsee.Ordinal)
            ? dsee.Value
            : current.DseeEnabled,
        FirmwareVersion = settings.FirmwareVersion is { } firmware && Claim(Setting.FirmwareVersion, firmware.Ordinal)
            ? firmware.Value
            : current.FirmwareVersion,
        Codec = settings.Codec is { } codec && Claim(Setting.Codec, codec.Ordinal)
            ? codec.Value
            : current.Codec,
    };

    private Task SendSettingAsync(
        Func<SonyV2Connection, CancellationToken, Task<long>> send,
        Setting setting,
        Func<DeviceState, DeviceState> apply,
        CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                // Stamped even when the value does not change, so an older echo cannot undo this SET afterwards.
                var acknowledged = await send(link, ct).ConfigureAwait(false);
                if (Claim(setting, acknowledged))
                {
                    Publish(apply(_state));
                }
            },
            cancellationToken);

    private Task RunConnectedAsync(
        Func<SonyV2Connection, CancellationToken, Task> operation,
        CancellationToken cancellationToken) =>
        RunAsync(
            ct => _link is Connected connected
                ? operation(connected.Link, ct)
                : throw new InvalidOperationException($"{Name} is not connected."),
            cancellationToken);

    private async Task RunAsync(Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        var operation = new Operation(body, cancellationToken);

        // Registered before the operation is queued, so a token that is already cancelled abandons it before the
        // actor can start it. A running operation observes the token itself.
        using var registration = cancellationToken.Register(operation.CancelIfQueued);
        if (!_mailbox.Writer.TryWrite(operation))
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        await operation.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunActorAsync()
    {
        await foreach (var message in _mailbox.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (message is Operation operation)
            {
                await ExecuteAsync(operation).ConfigureAwait(false);
                continue;
            }

            await GuardAsync(
                    async () =>
                    {
                        switch (message)
                        {
                            case NotificationArrived notification:
                                ApplyNotification(notification);
                                break;
                            case LinkDropped dropped:
                                await OnLinkDroppedAsync(dropped).ConfigureAwait(false);
                                break;
                        }
                    },
                    message.GetType().Name)
                .ConfigureAwait(false);
        }

        // Disposed: the mailbox is closed and every queued operation was refused.
        await GuardAsync(
                async () =>
                {
                    await ReleaseLinkAsync().ConfigureAwait(false);
                    Publish(DeviceState.Disconnected);
                },
                "Dispose")
            .ConfigureAwait(false);
    }

    // The actor must survive any failure here, or every later operation would hang.
    private async Task GuardAsync(Func<Task> step, string what)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling {Message} for {Name} failed", what, Name);
        }
    }

    // Links the operation to the controller lifetime and enforces the IHeadphoneDevice exception contract: platform
    // code below (WinRT via the connector) can throw types the UI does not expect.
    private async Task ExecuteAsync(Operation operation)
    {
        if (!operation.TryStart())
        {
            return;
        }

        var callerToken = operation.CallerToken;
        if (callerToken.IsCancellationRequested)
        {
            operation.Completion.TrySetCanceled(callerToken);
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            operation.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        try
        {
            await operation.Body(linked.Token).ConfigureAwait(false);
            operation.Completion.TrySetResult();
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            operation.Completion.TrySetCanceled(callerToken);
        }
        catch (OperationCanceledException ex)
        {
            // Not the caller's cancellation: either this controller is being disposed or the platform aborted the I/O.
            operation.Completion.TrySetException(_lifetime.IsCancellationRequested
                ? new ObjectDisposedException(GetType().Name)
                : new TransportException("The Bluetooth operation was aborted.", ex));
        }
        catch (Exception ex) when (IsContractFailure(ex))
        {
            operation.Completion.TrySetException(ex);
        }
        catch (Exception ex)
        {
            operation.Completion.TrySetException(
                new TransportException($"Unexpected failure talking to {Name}: {ex.Message}", ex));
        }
    }

    private void ApplyNotification(NotificationArrived notification)
    {
        // A notification from a link that was released while it waited in the mailbox describes nothing current.
        if (_link is not Connected connected || !ReferenceEquals(connected.Link, notification.Source))
        {
            return;
        }

        var (payload, ordinal) = notification.Received;
        try
        {
            var current = _state;
            if (!connected.Link.TryApplyNotification(current, payload.Span, out var next))
            {
                _logger.LogDebug("Ignored notification {Payload}", Convert.ToHexString(payload.Span));
                return;
            }

            // Only the settings this notification changes, and only where it is newer than what set them.
            var merged = current with
            {
                Battery = Differs(current.Battery, next.Battery) && Claim(Setting.Battery, ordinal)
                    ? next.Battery
                    : current.Battery,
                NoiseControl = Differs(current.NoiseControl, next.NoiseControl) && Claim(Setting.NoiseControl, ordinal)
                    ? next.NoiseControl
                    : current.NoiseControl,
                Equalizer = Differs(current.Equalizer, next.Equalizer) && Claim(Setting.Equalizer, ordinal)
                    ? next.Equalizer
                    : current.Equalizer,
                DseeEnabled = Differs(current.DseeEnabled, next.DseeEnabled) && Claim(Setting.Dsee, ordinal)
                    ? next.DseeEnabled
                    : current.DseeEnabled,
            };
            if (merged != current)
            {
                Publish(merged);
                return;
            }

            // The headset echoes every SET as a notification, which usually matches the state the ACK set.
            _logger.LogDebug(
                next == current
                    ? "Notification {Payload} confirms the current state"
                    : "Notification {Payload} is older than the current state",
                Convert.ToHexString(payload.Span));
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Malformed notification {Payload}", Convert.ToHexString(payload.Span));
        }
    }

    private async Task OnLinkDroppedAsync(LinkDropped dropped)
    {
        // A drop while connecting fails that connect instead; only the live link's loss changes the state here.
        if (_link is not Connected connected || !ReferenceEquals(connected.Link, dropped.Source))
        {
            return;
        }

        // The session already logged the drop with its cause; this only names the headset.
        _logger.LogInformation("{Name} disconnected because the link was lost", Name);
        await ReleaseLinkAsync().ConfigureAwait(false);
        Publish(DeviceState.Disconnected);
    }

    // Raised on the link's dispatch task; the actor handles them in order with everything else. After dispose the
    // mailbox is closed and the link is being released anyway, so a refused write needs no handling.
    private void OnNotificationReceived(object? sender, ReceivedPayload notification) =>
        _mailbox.Writer.TryWrite(new NotificationArrived((SonyV2Connection)sender!, notification));

    private void OnLinkLost(object? sender, Exception? cause) =>
        _mailbox.Writer.TryWrite(new LinkDropped((SonyV2Connection)sender!));

    private async Task ReleaseLinkAsync()
    {
        var current = _link;
        _link = new Disconnected();
        if (current is Connected connected)
        {
            await CloseAsync(connected.Link).ConfigureAwait(false);
        }
    }

    private async Task CloseAsync(SonyV2Connection link)
    {
        link.NotificationReceived -= OnNotificationReceived;
        link.LinkLost -= OnLinkLost;
        await link.DisposeAsync().ConfigureAwait(false);
    }

    // Only the actor publishes, so the read-modify-write around it needs no lock.
    private void Publish(DeviceState updated)
    {
        if (updated == _state)
        {
            return;
        }

        _state = updated;
        try
        {
            StateChanged?.Invoke(this, updated);
        }
        catch (Exception ex)
        {
            // A subscriber's bug must not fail an operation the headset already applied, nor stop the actor.
            _logger.LogError(ex, "A StateChanged handler of {Name} failed", Name);
        }
    }

    private enum Setting
    {
        Battery,
        NoiseControl,
        Equalizer,
        Dsee,
        FirmwareVersion,
        Codec,
    }

    // The connection lifecycle. Only Connected holds a link, so no operation can reach one in another state.
    private abstract record LinkState;

    private sealed record Disconnected : LinkState;

    private sealed record Connecting : LinkState;

    private sealed record Connected(SonyV2Connection Link) : LinkState;

    private sealed record Failed : LinkState;

    private abstract class Message;

    private sealed class NotificationArrived(SonyV2Connection source, ReceivedPayload received) : Message
    {
        public SonyV2Connection Source { get; } = source;

        public ReceivedPayload Received { get; } = received;
    }

    private sealed class LinkDropped(SonyV2Connection source) : Message
    {
        public SonyV2Connection Source { get; } = source;
    }

    private sealed class Operation(Func<CancellationToken, Task> body, CancellationToken callerToken) : Message
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Abandoned = 2;

        private int _stage;

        public Func<CancellationToken, Task> Body { get; } = body;

        public CancellationToken CallerToken { get; } = callerToken;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryStart() => Interlocked.CompareExchange(ref _stage, Running, Queued) == Queued;

        public void CancelIfQueued()
        {
            if (Interlocked.CompareExchange(ref _stage, Abandoned, Queued) == Queued)
            {
                Completion.TrySetCanceled(CallerToken);
            }
        }
    }
}
