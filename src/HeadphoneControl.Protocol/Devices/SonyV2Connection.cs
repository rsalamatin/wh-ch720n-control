using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Framing;
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
/// Requests are serialized by the session. Failures are the session's: <see cref="TimeoutException"/> when no ACK or
/// response arrives in time, <see cref="TransportException"/> when the link is gone, <see cref="FormatException"/> for a
/// malformed reply. Events are raised in order from the session's dispatch task, never after
/// <see cref="DisposeAsync"/> returns.
/// </remarks>
public sealed class SonyV2Connection : IAsyncDisposable
{
    private readonly ProtocolSession _session;
    private readonly ILogger<SonyV2Connection> _logger;
    private volatile bool _linkLost;

    // Set by OpenAsync before the instance is handed out, so every public member sees it.
    private V2CommandSet _commands = null!;

    // Subscribes before the session starts, so a link loss during the handshake is latched in IsLinkLost.
    private SonyV2Connection(ProtocolSession session, ILogger<SonyV2Connection> logger)
    {
        _session = session;
        _logger = logger;
        _session.NotificationReceived += OnNotificationReceived;
        _session.Disconnected += OnDisconnected;
    }

    /// <summary>An unsolicited DATA_MDR payload, e.g. a battery change or the echo the headset sends after every SET.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? NotificationReceived;

    /// <summary>The link dropped or the headset closed it. Raised at most once; <see cref="IsLinkLost"/> is set first.</summary>
    public event EventHandler<Exception?>? LinkLost;

    /// <summary>
    /// True once the link dropped, even if it dropped before a handler subscribed to <see cref="LinkLost"/>.
    /// </summary>
    public bool IsLinkLost => _linkLost;

    /// <summary>
    /// Starts a session over <paramref name="transport"/>, sends the init handshake and confirms a V2 device.
    /// Throws <see cref="NotSupportedException"/> when either signal is not V2; then only the handshake was sent.
    /// On any failure the transport is disposed.
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
            connection._commands = V2CommandSet.FromHandshake(transport.ServiceGeneration, reply.Span);
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
        var dsee = await TryQueryValueAsync(_commands.QueryDsee(), _commands.ParseDsee, cancellationToken)
            .ConfigureAwait(false);
        var firmware = await TryQueryAsync(
                _commands.QueryFirmwareVersion(), _commands.ParseFirmwareVersion, cancellationToken)
            .ConfigureAwait(false);
        var codec = await TryQueryValueAsync(_commands.QueryCodec(), _commands.ParseCodec, cancellationToken)
            .ConfigureAwait(false);
        return new DeviceSettings(battery, noise, equalizer, dsee, firmware, codec);
    }

    /// <summary>Reads the equalizer; null when the query is unanswered or malformed.</summary>
    public Task<EqualizerState?> TryReadEqualizerAsync(CancellationToken cancellationToken) =>
        TryQueryAsync(_commands.QueryEqualizer(), _commands.ParseEqualizer, cancellationToken);

    /// <summary>Completes when the headset ACKs the change.</summary>
    public Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return SendAsync(_commands.SetNoiseControl(state), cancellationToken);
    }

    /// <summary>
    /// Completes when the headset ACKs the preset. The headset owns each preset's band curve, so the caller should
    /// re-read it with <see cref="TryReadEqualizerAsync"/>.
    /// </summary>
    public Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken) =>
        SendAsync(_commands.SetEqualizerPreset(preset), cancellationToken);

    /// <summary>Completes when the headset ACKs the manual curve (each level -10..10, 5 bands).</summary>
    public Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return SendAsync(_commands.SetEqualizerCustom(clearBass, bands), cancellationToken);
    }

    /// <summary>Completes when the headset ACKs the change.</summary>
    public Task SetDseeAsync(bool enabled, CancellationToken cancellationToken) =>
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

    private Task SendAsync(MdrRequest request, CancellationToken cancellationToken) =>
        _session.SendAsync(request.Payload, cancellationToken);

    private async Task<T?> TryQueryAsync<T>(MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
        where T : class =>
        await TryQueryCoreAsync(request, parse, cancellationToken).ConfigureAwait(false) is (true, var value)
            ? value
            : null;

    private async Task<T?> TryQueryValueAsync<T>(
        MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
        where T : struct =>
        await TryQueryCoreAsync(request, parse, cancellationToken).ConfigureAwait(false) is (true, var value)
            ? value
            : null;

    // A single unanswered or malformed query must not fail the whole connection: the feature stays unknown
    // (null) and the UI disables it. Link loss still propagates.
    private async Task<(bool Found, T Value)> TryQueryCoreAsync<T>(
        MdrRequest request, SpanParser<T> parse, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _session.RequestAsync(
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

    private void OnNotificationReceived(object? sender, Frame frame) =>
        NotificationReceived?.Invoke(this, frame.Payload);

    private void OnDisconnected(object? sender, Exception? cause)
    {
        _linkLost = true;
        LinkLost?.Invoke(this, cause);
    }

    private delegate T SpanParser<out T>(ReadOnlySpan<byte> payload);
}
