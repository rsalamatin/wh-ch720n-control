using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Devices;

// Exists only once both the service and the init reply said V2; unusable after the link drops (open a new one).
// Async members fail only with TransportException (link gone, or disposed mid-request), TimeoutException (link may
// still be usable), FormatException, NotSupportedException (OpenAsync only), ArgumentException,
// ObjectDisposedException or OperationCanceledException.
// The session serializes requests. Events are raised in order from the session's dispatch task, never after DisposeAsync returns.
internal sealed class SonyV2Connection : IAsyncDisposable
{
    private readonly ProtocolSession _session;
    private readonly ILogger<SonyV2Connection> _logger;
    private volatile bool _linkLost;

    // Set by OpenAsync before the instance is handed out, so every public member sees it.
    private V2CommandSet _commands = null!;

    // Subscribes before the session starts, so a link loss before the owner subscribes is still latched in IsLinkLost.
    private SonyV2Connection(ProtocolSession session, ILogger<SonyV2Connection> logger)
    {
        _session = session;
        _logger = logger;
        _session.NotificationReceived += OnNotificationReceived;
        _session.Disconnected += OnDisconnected;
    }

    // Includes the echo the headset sends after every SET. The ordinal orders it against replies and ACKs.
    public event EventHandler<ReceivedPayload>? NotificationReceived;

    // Raised at most once; IsLinkLost is set first.
    public event EventHandler<Exception?>? LinkLost;

    public bool IsLinkLost => _linkLost;

    // On any failure after the arguments are validated, the transport is disposed.
    public static async Task<SonyV2Connection> OpenAsync(
        TransportConnection transport,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var session = new ProtocolSession(
            transport.Transport, loggerFactory.CreateLogger<ProtocolSession>(), timeProvider);
        var connection = new SonyV2Connection(session, loggerFactory.CreateLogger<SonyV2Connection>());
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);

            // The init opcode 0x00 is harmless on every generation, so it is the only thing sent before the check.
            var handshake = ProtocolHandshake.CreateRequest();
            var reply = await session.RequestAsync(
                handshake.Payload, handshake.ResponseOpcode!.Value, handshake.ResponseSubtype, cancellationToken)
                .ConfigureAwait(false);
            connection._commands = V2CommandSet.FromHandshake(transport.ServiceGeneration, reply.Payload.Span);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DeviceSettings> ReadAllAsync(CancellationToken cancellationToken)
    {
        var battery = await TryQueryAsync(_commands.QueryBattery(), _commands.ParseBattery, cancellationToken)
            .ConfigureAwait(false);
        var noise = await TryQueryAsync(_commands.QueryNoiseControl(), _commands.ParseNoiseControl, cancellationToken)
            .ConfigureAwait(false);
        var equalizer = await TryQueryAsync(_commands.QueryEqualizer(), _commands.ParseEqualizer, cancellationToken)
            .ConfigureAwait(false);
        var dsee = await TryQueryAsync(_commands.QueryDsee(), _commands.ParseDsee, cancellationToken)
            .ConfigureAwait(false);
        var firmware = await TryQueryAsync(
                _commands.QueryFirmwareVersion(), _commands.ParseFirmwareVersion, cancellationToken)
            .ConfigureAwait(false);
        var codec = await TryQueryAsync(_commands.QueryCodec(), _commands.ParseCodec, cancellationToken)
            .ConfigureAwait(false);
        return new DeviceSettings(battery, noise, equalizer, dsee, firmware, codec);
    }

    public Task<Received<EqualizerState>?> TryReadEqualizerAsync(CancellationToken cancellationToken) =>
        TryQueryAsync(_commands.QueryEqualizer(), _commands.ParseEqualizer, cancellationToken);

    // The setters complete with the receive ordinal of the headset's ACK.
    public Task<long> SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(_commands.SetNoiseControl(state), cancellationToken);
    }

    // The headset owns each preset's band curve, so the caller should re-read it with TryReadEqualizerAsync.
    public Task<long> SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        SendAsync(_commands.SetEqualizerPreset(preset), cancellationToken);

    public Task<long> SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return SendAsync(_commands.SetEqualizerCustom(clearBass, bands), cancellationToken);
    }

    public Task<long> SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendAsync(_commands.SetDsee(enabled), cancellationToken);

    public bool TryApplyNotification(DeviceState current, ReadOnlySpan<byte> payload, out DeviceState updated) =>
        _commands.TryApplyNotification(current, payload, out updated);

    // Closes the session and the transport without raising LinkLost.
    public async ValueTask DisposeAsync()
    {
        _session.NotificationReceived -= OnNotificationReceived;
        _session.Disconnected -= OnDisconnected;
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private Task<long> SendAsync(MdrRequest request, CancellationToken cancellationToken) =>
        _session.SendAsync(request.Payload, cancellationToken);

    // An unanswered or malformed query leaves the setting unknown (null) instead of failing the connection;
    // link loss still propagates.
    private async Task<Received<T>?> TryQueryAsync<T>(
        MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _session.RequestAsync(
                request.Payload, request.ResponseOpcode!.Value, request.ResponseSubtype, cancellationToken)
                .ConfigureAwait(false);
            return new Received<T>(parse(reply.Payload.Span), reply.Ordinal);
        }
        catch (Exception ex) when (ex is TimeoutException or FormatException)
        {
            _logger.LogWarning(ex, "Query {Payload} failed; the setting stays unknown", Convert.ToHexString(request.Payload.Span));
            return null;
        }
    }

    private void OnNotificationReceived(object? sender, ReceivedPayload notification) =>
        NotificationReceived?.Invoke(this, notification);

    private void OnDisconnected(object? sender, Exception? cause)
    {
        _linkLost = true;
        LinkLost?.Invoke(this, cause);
    }

    private delegate T SpanParser<out T>(ReadOnlySpan<byte> payload);
}
