namespace HeadphoneControl.Protocol.Devices;

// A null member was unanswered or malformed and is unknown.
internal sealed record DeviceSettings(
    Received<BatteryState>? Battery,
    Received<NoiseControlState>? NoiseControl,
    Received<EqualizerState>? Equalizer,
    Received<bool>? DseeEnabled,
    Received<string>? FirmwareVersion,
    Received<AudioCodec>? Codec);
