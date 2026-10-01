namespace HeadphoneControl.Protocol.Devices;

/// <summary>Sony protocol generation. Opcode 0x22 is BATTERY on V2 but POWER OFF on V1.</summary>
public enum ProtocolGeneration
{
    Unknown,
    V1,
    V2,
}

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Failed,
}

public enum NoiseControlMode
{
    Off,
    NoiseCancelling,
    Ambient,
}

/// <summary>Equalizer preset ids as sent on the wire (spec section 7.3).</summary>
public enum EqualizerPreset : byte
{
    Off = 0x00,
    Bright = 0x10,
    Excited = 0x11,
    Mellow = 0x12,
    Relaxed = 0x13,
    Vocal = 0x14,
    TrebleBoost = 0x15,
    BassBoost = 0x16,
    Speech = 0x17,
    Manual = 0xA0,
}

public enum AudioCodec
{
    Unknown,
    Sbc,
    Aac,
    Ldac,
    AptX,
    AptXHd,
    Lc3,
}

/// <summary>Battery level 0..100.</summary>
public sealed record BatteryState(int Level, bool IsCharging);

/// <summary>Ambient level is 1..20 and only meaningful in <see cref="NoiseControlMode.Ambient"/>.</summary>
public sealed record NoiseControlState(NoiseControlMode Mode, bool FocusOnVoice, int AmbientLevel);

/// <summary>Clear bass and each of the 5 bands are in -10..10 (wire value minus 10).</summary>
/// <remarks>Equality compares the band levels, not the list instance.</remarks>
public sealed record EqualizerState(EqualizerPreset Preset, int ClearBass, IReadOnlyList<int> Bands)
{
    public bool Equals(EqualizerState? other) =>
        other is not null && Preset == other.Preset && ClearBass == other.ClearBass && Bands.SequenceEqual(other.Bands);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Preset);
        hash.Add(ClearBass);
        foreach (var band in Bands)
        {
            hash.Add(band);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Snapshot of what is known about the headset; null members are not (yet) known.</summary>
public sealed record DeviceState(
    ConnectionStatus Connection,
    ProtocolGeneration Generation,
    BatteryState? Battery,
    NoiseControlState? NoiseControl,
    EqualizerState? Equalizer,
    bool? DseeEnabled,
    string? FirmwareVersion,
    AudioCodec? Codec)
{
    public static DeviceState Disconnected { get; } =
        new(ConnectionStatus.Disconnected, ProtocolGeneration.Unknown, null, null, null, null, null, null);
}
