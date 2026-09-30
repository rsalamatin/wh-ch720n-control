using System.Threading.Channels;
using HeadphoneControl.Protocol.Devices;
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
/// that operation.
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

    // Only the actor touches _link and writes _state; State reads _state from any thread.
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
                Publish(_ => DeviceState.Disconnected);
            },
            cancellationToken);

    public Task RefreshAsync(CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                var settings = await link.ReadAllAsync(ct).ConfigureAwait(false);
                Publish(settings.ApplyTo);
            },
            cancellationToken);

    public Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendSettingAsync(
            (link, ct) => link.SetNoiseControlAsync(state, ct),
            current => current with { NoiseControl = NormalizeNoiseControl(state) },
            cancellationToken);
    }

    public Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                await link.SetEqualizerPresetAsync(preset, ct).ConfigureAwait(false);

                // The device owns each preset's band curve, so it is re-read. Once the SET is ACKed the caller's
                // cancellation no longer applies: abandoning the re-read would leave the old preset's bands showing.
                var equalizer = await link.TryReadEqualizerAsync(_lifetime.Token).ConfigureAwait(false);
                if (equalizer is null)
                {
                    _logger.LogWarning("Equalizer of {Name} is unknown after switching to {Preset}", Name, preset);
                }

                // Unknown rather than the previous preset's bands, which would let a later band edit send a curve the
                // user never saw. A late reply or notification is queued behind this operation, so it still wins.
                Publish(current => current with { Equalizer = equalizer });
            },
            cancellationToken);

    public Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var snapshot = bands.ToArray();
        return SendSettingAsync(
            (link, ct) => link.SetCustomEqualizerAsync(clearBass, snapshot, ct),
            current => current with { Equalizer = new EqualizerState(EqualizerPreset.Manual, clearBass, snapshot) },
            cancellationToken);
    }

    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendSettingAsync(
            (link, ct) => link.SetDseeAsync(enabled, ct),
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

    private async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        if (_link is Connected)
        {
            return;
        }

        _link = new Connecting();
        Publish(_ => DeviceState.Disconnected with { Connection = ConnectionStatus.Connecting });
        SonyV2Connection? link = null;
        try
        {
            link = await OpenLinkAsync(cancellationToken).ConfigureAwait(false);
            var settings = await link.ReadAllAsync(cancellationToken).ConfigureAwait(false);

            // The link's LinkDropped message waits behind this operation, so a drop after the last reply is caught
            // here, and a later one finds Connected and clears it.
            if (link.IsLinkLost)
            {
                throw new TransportException($"The link to {Name} dropped while connecting.");
            }

            // Connected and the settings are published together, so the UI never sees "connected, all unknown".
            _link = new Connected(link);
            Publish(current => settings.ApplyTo(current) with
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

            _link = cancelled ? new Disconnected() : new Failed(ex);
            Publish(_ => cancelled
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

    private Task SendSettingAsync(
        Func<SonyV2Connection, CancellationToken, Task> send,
        Func<DeviceState, DeviceState> apply,
        CancellationToken cancellationToken) =>
        RunConnectedAsync(
            async (link, ct) =>
            {
                await send(link, ct).ConfigureAwait(false);
                Publish(apply);
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
        if (!_mailbox.Writer.TryWrite(operation))
        {
            throw new ObjectDisposedException(GetType().Name);
        }

        // A caller that gives up while its operation is still queued is released at once; a running operation
        // observes the token itself.
        using var registration = cancellationToken.Register(operation.CancelIfQueued);
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

            try
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
            }
            catch (Exception ex)
            {
                // The actor must survive a failing StateChanged handler, or every later operation would hang.
                _logger.LogError(ex, "Handling {Message} for {Name} failed", message.GetType().Name, Name);
            }
        }

        // Disposed: the mailbox is closed and every queued operation was refused.
        await ReleaseLinkAsync().ConfigureAwait(false);
        Publish(_ => DeviceState.Disconnected);
    }

    // Links the operation to the controller lifetime and enforces the IHeadphoneDevice exception contract: platform
    // code below (WinRT via the connector) can throw types the UI does not expect.
    private async Task ExecuteAsync(Operation operation)
    {
        if (!operation.TryStart())
        {
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            operation.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
            return;
        }

        var callerToken = operation.CallerToken;
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

        var payload = notification.Payload.Span;
        try
        {
            var recognised = connected.Link.TryApplyNotification(_state, payload, out var next);
            if (recognised && next != _state)
            {
                Publish(_ => next);
                return;
            }

            // The headset echoes every SET as a notification, which usually matches the state the ACK set.
            _logger.LogDebug(
                recognised ? "Notification {Payload} confirms the current state" : "Ignored notification {Payload}",
                Convert.ToHexString(payload));
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Malformed notification {Payload}", Convert.ToHexString(payload));
        }
    }

    private async Task OnLinkDroppedAsync(LinkDropped dropped)
    {
        // A drop while connecting fails that connect instead; only the live link's loss changes the state here.
        if (_link is not Connected connected || !ReferenceEquals(connected.Link, dropped.Source))
        {
            return;
        }

        _logger.LogWarning(dropped.Cause, "Link to {Name} lost", Name);
        await ReleaseLinkAsync().ConfigureAwait(false);
        Publish(_ => DeviceState.Disconnected);
    }

    // Raised on the link's dispatch task; the actor handles them in order with everything else. After dispose the
    // mailbox is closed and the link is being released anyway, so a refused write needs no handling.
    private void OnNotificationReceived(object? sender, ReadOnlyMemory<byte> payload) =>
        _mailbox.Writer.TryWrite(new NotificationArrived((SonyV2Connection)sender!, payload));

    private void OnLinkLost(object? sender, Exception? cause) =>
        _mailbox.Writer.TryWrite(new LinkDropped((SonyV2Connection)sender!, cause));

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

    // Only the actor publishes, so the read-modify-write needs no lock.
    private void Publish(Func<DeviceState, DeviceState> change)
    {
        var updated = change(_state);
        if (updated == _state)
        {
            return;
        }

        _state = updated;
        StateChanged?.Invoke(this, updated);
    }

    // The connection lifecycle. Only Connected holds a link, so no operation can reach one in another state.
    private abstract record LinkState;

    private sealed record Disconnected : LinkState;

    private sealed record Connecting : LinkState;

    private sealed record Connected(SonyV2Connection Link) : LinkState;

    private sealed record Failed(Exception Cause) : LinkState;

    private abstract record Message;

    private sealed record NotificationArrived(SonyV2Connection Source, ReadOnlyMemory<byte> Payload) : Message;

    private sealed record LinkDropped(SonyV2Connection Source, Exception? Cause) : Message;

    private sealed record Operation(Func<CancellationToken, Task> Body, CancellationToken CallerToken) : Message
    {
        private const int Queued = 0;
        private const int Running = 1;
        private const int Abandoned = 2;

        private int _stage;

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
