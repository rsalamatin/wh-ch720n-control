namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// What the UI binds to. Setters complete once the device has acknowledged the change; <see cref="State"/> is then updated.
/// </summary>
/// <remarks>
/// Every async member fails only with one of these types, so callers can catch exactly this set:
/// <list type="bullet">
/// <item><see cref="IOException"/> (incl. <c>TransportException</c>): the Bluetooth link failed or dropped.</item>
/// <item><see cref="TimeoutException"/>: no ACK or response in time; the link may still be usable.</item>
/// <item><see cref="NotSupportedException"/>: the device is not a confirmed V2 device; nothing was sent.</item>
/// <item><see cref="FormatException"/> (incl. <c>ProtocolFormatException</c>): the device sent a malformed reply.</item>
/// <item><see cref="InvalidOperationException"/>: called in the wrong connection state.</item>
/// <item><see cref="ArgumentException"/>: an argument is out of range (a caller bug).</item>
/// <item><see cref="OperationCanceledException"/>: the caller's token was cancelled.</item>
/// </list>
/// </remarks>
public interface IHeadphoneDevice : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Latest snapshot; replaced (never mutated) on every change.</summary>
    DeviceState State { get; }

    /// <summary>
    /// Raised after every change, possibly on a background thread. Events from different threads can arrive out of
    /// order, so handlers should read <see cref="State"/> for the latest snapshot rather than trust the argument.
    /// </summary>
    event EventHandler<DeviceState>? StateChanged;

    /// <summary>Connects, performs the init handshake, confirms the protocol generation and reads all state.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>Re-reads every supported setting from the device.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);

    Task SetNoiseControlAsync(NoiseControlState state, CancellationToken cancellationToken);

    /// <summary>
    /// Selects a preset, then re-reads the band curve the device assigned to it. If that read fails, the call still
    /// succeeds and <see cref="DeviceState.Equalizer"/> becomes null (unknown) until the next refresh or notification.
    /// </summary>
    Task SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken);

    /// <summary>Switches to <see cref="EqualizerPreset.Manual"/> with the given levels (each -10..10, 5 bands).</summary>
    Task SetCustomEqualizerAsync(int clearBass, IReadOnlyList<int> bands, CancellationToken cancellationToken);

    Task SetDseeAsync(bool enabled, CancellationToken cancellationToken);
}
