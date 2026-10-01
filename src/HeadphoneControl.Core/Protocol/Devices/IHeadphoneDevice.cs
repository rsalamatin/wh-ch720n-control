namespace HeadphoneControl.Protocol.Devices;

/// <summary>Setters complete once the device has acknowledged the change; <see cref="State"/> is then updated.</summary>
/// <remarks>
/// <para>
/// Operations run one at a time, in call order. A setter call that a newer call of the same <see cref="SettingGroup"/>
/// replaces before it is sent completes with <see cref="EditOutcome.Superseded"/>, even if the link has dropped.
/// </para>
/// <para>
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
/// </para>
/// </remarks>
public interface IHeadphoneDevice : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Latest snapshot; replaced (never mutated) on every change.</summary>
    DeviceState State { get; }

    /// <summary>
    /// Raised after every change and when a group's last pending edit completes, possibly on a background thread and
    /// out of order, so handlers should read <see cref="State"/> rather than trust the argument.
    /// </summary>
    event EventHandler<DeviceState>? StateChanged;

    /// <summary>Connects, confirms a V2 device and reads all state.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);

    /// <summary>
    /// True while a setter of <paramref name="group"/> is outstanding; <see cref="State"/> may then lag the user's
    /// value. <see cref="StateChanged"/> is raised when it becomes false.
    /// </summary>
    bool HasPendingEdit(SettingGroup group);

    Task<EditOutcome> SetNoiseControlAsync(
        NoiseControlState state, EditPacing pacing, CancellationToken cancellationToken);

    /// <summary>
    /// Selects a preset, then re-reads the band curve the device assigned to it. If that read fails, the call still
    /// succeeds and <see cref="DeviceState.Equalizer"/> becomes null (unknown) until the next refresh or notification.
    /// </summary>
    Task<EditOutcome> SetEqualizerPresetAsync(EqualizerPreset preset, CancellationToken cancellationToken);

    /// <summary>Switches to <see cref="EqualizerPreset.Manual"/> with the given levels (each -10..10, 5 bands).</summary>
    Task<EditOutcome> SetCustomEqualizerAsync(
        int clearBass, IReadOnlyList<int> bands, EditPacing pacing, CancellationToken cancellationToken);

    Task<EditOutcome> SetDseeAsync(bool enabled, CancellationToken cancellationToken);
}
