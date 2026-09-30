namespace HeadphoneControl.Protocol.Devices;

/// <summary>One read of every setting. A null member was not answered (or was malformed) and is unknown.</summary>
public sealed record DeviceSettings(
    BatteryState? Battery,
    NoiseControlState? NoiseControl,
    EqualizerState? Equalizer,
    bool? DseeEnabled,
    string? FirmwareVersion,
    AudioCodec? Codec)
{
    /// <summary>
    /// Overlays the answered settings on <paramref name="current"/>. Unanswered ones keep what the state already has,
    /// e.g. a value a notification delivered meanwhile.
    /// </summary>
    public DeviceState ApplyTo(DeviceState current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            Battery = Battery ?? current.Battery,
            NoiseControl = NoiseControl ?? current.NoiseControl,
            Equalizer = Equalizer ?? current.Equalizer,
            DseeEnabled = DseeEnabled ?? current.DseeEnabled,
            FirmwareVersion = FirmwareVersion ?? current.FirmwareVersion,
            Codec = Codec ?? current.Codec,
        };
    }
}
