using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// One confirmed V2 link: a started <see cref="ProtocolSession"/> plus the <see cref="V2CommandSet"/> the handshake
/// unlocked. It only exists after both the transport's service and the init reply said V2, and it is unusable once the
/// link drops; open a new one to reconnect.
/// </summary>
/// <remarks>
/// <para>
/// Requests are serialized by the session. Every async member fails only with one of these types:
/// </para>
/// <list type="bullet">
/// <item><see cref="TransportException"/>: the link is gone, or the connection was disposed while a request was in
/// flight.</item>
/// <item><see cref="TimeoutException"/>: no ACK or response in time; the link may still be usable.</item>
/// <item><see cref="FormatException"/> (incl. <c>ProtocolFormatException</c>): a malformed reply.</item>
/// <item><see cref="NotSupportedException"/>: <see cref="OpenAsync"/> only; the device is not a confirmed V2 device.</item>
/// <item><see cref="ArgumentException"/> (incl. <see cref="ArgumentNullException"/> and
/// <see cref="ArgumentOutOfRangeException"/>): a null argument or a value the command builders reject.</item>
/// <item><see cref="ObjectDisposedException"/>: called after <see cref="DisposeAsync"/>.</item>
/// <item><see cref="OperationCanceledException"/>: the caller's token was cancelled.</item>
/// </list>
/// <para>
/// Events are raised in order from the session's dispatch task, never after <see cref="DisposeAsync"/> returns.
/// </para>
/// </remarks>
public sealed class SonyV2Connection : IAsyncDisposable
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

    /// <summary>
    /// An unsolicited DATA_MDR payload, e.g. a battery change or the echo the headset sends after every SET. Its
    /// ordinal orders it against the replies and ACKs the other members return.
    /// </summary>
    public event EventHandler<ReceivedPayload>? NotificationReceived;

    /// <summary>The link dropped or the headset closed it. Raised at most once; <see cref="IsLinkLost"/> is set first.</summary>
    public event EventHandler<Exception?>? LinkLost;

    /// <summary>
    /// True once the link dropped, even if it dropped before a handler subscribed to <see cref="LinkLost"/>.
    /// </summary>
    public bool IsLinkLost => _linkLost;

    /// <summary>
    /// Starts a session over <paramref name="transport"/>, sends the init handshake and confirms a V2 device.
    /// Throws <see cref="NotSupportedException"/> when either signal is not V2; then only the handshake was sent.
    /// On any failure after the arguments are validated, the transport is disposed.
    /// </summary>
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

    /// <summary>
    /// Queries every setting in turn. A single unanswered or malformed query does not fail the read: that setting is
    /// null. Link loss still propagates as <see cref="TransportException"/>.
    /// </summary>
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

    /// <summary>Reads the equalizer; null when the query is unanswered or malformed.</summary>
    public Task<Received<EqualizerState>?> TryReadEqualizerAsync(CancellationToken cancellationToken) =>
        TryQueryAsync(_commands.QueryEqualizer(), _commands.ParseEqualizer, cancellationToken);

    /// <summary>Completes with the receive ordinal of the headset's ACK.</summary>
    public Task<long> SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(_commands.SetNoiseControl(state), cancellationToken);
    }

    /// <summary>
    /// Completes with the receive ordinal of the headset's ACK. The headset owns each preset's band curve, so the
    /// caller should re-read it with <see cref="TryReadEqualizerAsync"/>.
    /// </summary>
    public Task<long> SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        SendAsync(_commands.SetEqualizerPreset(preset), cancellationToken);

    /// <summary>
    /// Sets the manual curve (each level -10..10, 5 bands). Completes with the receive ordinal of the headset's ACK.
    /// </summary>
    public Task<long> SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return SendAsync(_commands.SetEqualizerCustom(clearBass, bands), cancellationToken);
    }

    /// <summary>Completes with the receive ordinal of the headset's ACK.</summary>
    public Task<long> SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
        SendAsync(_commands.SetDsee(enabled), cancellationToken);

    /// <summary>
    /// Applies a <see cref="NotificationReceived"/> payload to <paramref name="current"/>. Returns false for a payload
    /// that is not a known notification. Throws <see cref="FormatException"/> for a malformed one.
    /// </summary>
    public bool TryApplyNotification(DeviceState current, ReadOnlySpan<byte> payload, out DeviceState updated) =>
        _commands.TryApplyNotification(current, payload, out updated);

    /// <summary>Closes the session and the transport. <see cref="LinkLost"/> is not raised for this.</summary>
    public async ValueTask DisposeAsync()
    {
        _session.NotificationReceived -= OnNotificationReceived;
        _session.Disconnected -= OnDisconnected;
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private Task<long> SendAsync(MdrRequest request, CancellationToken cancellationToken) =>
        _session.SendAsync(request.Payload, cancellationToken);

    // A single unanswered or malformed query must not fail the whole connection: the feature stays unknown
    // (null) and the UI disables it. Link loss still propagates.
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
