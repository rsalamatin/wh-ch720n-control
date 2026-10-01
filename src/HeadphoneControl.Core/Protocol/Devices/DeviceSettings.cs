namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// One read of every setting, each with the receive ordinal of its reply. A null member was not answered (or was
/// malformed) and is unknown.
/// </summary>
internal sealed record DeviceSettings(
    Received<BatteryState>? Battery,
    Received<NoiseControlState>? NoiseControl,
    Received<EqualizerState>? Equalizer,
    Received<bool>? DseeEnabled,
    Received<string>? FirmwareVersion,
    Received<AudioCodec>? Codec);
