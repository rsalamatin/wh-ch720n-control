using System.Text;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Simulation;

// The device side of a WH-CH720N on firmware 1.1.4, at payload level: answers queries and applies SETs the way the
// captured sessions in docs/development-status.md show. It outlives each link, so a reconnect sees the last settings.
// The opcodes mirror the internal V2Opcodes of the protocol layer; none exists only here.
internal sealed class SimulatedHeadset
{
    private const byte InitRequest = 0x00;
    private const byte FirmwareGet = 0x04;
    private const byte CodecGet = 0x12;
    private const byte BatteryGet = 0x22;
    private const byte EqualizerGet = 0x56;
    private const byte EqualizerSet = 0x58;
    private const byte NoiseControlGet = 0x66;
    private const byte NoiseControlSet = 0x68;
    private const byte DseeGet = 0xE6;
    private const byte DseeSet = 0xE8;

    private const byte NoiseControlSubtype = 0x17;
    private const byte NoiseControlVersion = 0x01;
    private const byte EqualizerValueCount = 0x06;
    private const byte DseeSubtype = 0x01;
    private const byte AacCodec = 0x02;
    private const int EqualizerWireOffset = 10;
    private const int EqualizerBandCount = 5;

    // Captured reply of the real headset to 00 00.
    private static readonly byte[] InitReply = [0x01, 0x00, 0x03, 0x00, 0x10, 0x02, 0x00, 0x00];

    // Captured after every DSEE SET alongside the E9 echo; the app ignores it, so it exercises the unknown-frame path.
    private static readonly byte[] DseeSideEffect = [0xE5, 0x01, 0x00];

    // Clear bass and 400 Hz .. 16 kHz per preset. Bright and Bass Boost are the captured curves; the others are
    // plausible, since the real values live in the firmware.
    private static readonly Dictionary<EqualizerPreset, (int ClearBass, int[] Bands)> PresetCurves = new()
    {
        [EqualizerPreset.Off] = (0, [0, 0, 0, 0, 0]),
        [EqualizerPreset.Bright] = (-1, [0, 5, 7, 7, 9]),
        [EqualizerPreset.Excited] = (0, [3, 1, 0, 2, 4]),
        [EqualizerPreset.Mellow] = (0, [2, 1, 0, -2, -4]),
        [EqualizerPreset.Relaxed] = (0, [-1, -2, -2, -3, -4]),
        [EqualizerPreset.Vocal] = (0, [-2, 2, 4, 2, -1]),
        [EqualizerPreset.TrebleBoost] = (0, [0, 0, 1, 4, 6]),
        [EqualizerPreset.BassBoost] = (7, [0, 0, 0, 0, 0]),
        [EqualizerPreset.Speech] = (0, [-4, 2, 4, 2, -3]),
    };

    private readonly Lock _gate = new();

    private readonly byte _batteryLevel = 80;
    private readonly byte[] _firmware = Encoding.ASCII.GetBytes("1.1.4");
    private byte _noiseEffect = 1;
    private byte _noiseSettingType;
    private byte _focusOnVoice;
    private byte _ambientLevel = 10;
    private byte _equalizerPreset = (byte)EqualizerPreset.Off;
    private int _clearBass;
    private int[] _bands = [0, 0, 0, 0, 0];
    private byte _dsee;

    // Reply is null when the headset only ACKs. Unknown requests are only ACKed, like the unanswered auto power-off
    // query on the real headset.
    public Reaction Handle(ReadOnlySpan<byte> request)
    {
        if (request.Length < 2)
        {
            return Reaction.AckOnly;
        }

        lock (_gate)
        {
            return request[0] switch
            {
                InitRequest => new Reaction(InitReply, []),
                BatteryGet => new Reaction([(byte)(BatteryGet + 1), request[1], _batteryLevel, 0], []),
                NoiseControlGet => new Reaction(NoiseControl(NoiseControlGet + 1), []),
                NoiseControlSet => SetNoiseControl(request),
                EqualizerGet => new Reaction(Equalizer(), []),
                EqualizerSet => SetEqualizer(request),
                DseeGet => new Reaction([(byte)(DseeGet + 1), DseeSubtype, _dsee], []),
                DseeSet => SetDsee(request),
                FirmwareGet => new Reaction([(byte)(FirmwareGet + 1), request[1], (byte)_firmware.Length, .. _firmware], []),
                CodecGet => new Reaction([(byte)(CodecGet + 1), request[1], AacCodec], []),
                _ => Reaction.AckOnly,
            };
        }
    }

    private static byte ToWire(int level) => (byte)(level + EqualizerWireOffset);

    // Caller holds _gate.
    private byte[] NoiseControl(int opcode) =>
        [(byte)opcode, NoiseControlSubtype, NoiseControlVersion, _noiseEffect, _noiseSettingType, _focusOnVoice, _ambientLevel];

    // Caller holds _gate.
    private byte[] Equalizer() =>
        [(byte)(EqualizerGet + 1), 0x00, _equalizerPreset, EqualizerValueCount, ToWire(_clearBass), .. _bands.Select(ToWire)];

    // Caller holds _gate. The real headset echoes every noise-control SET as a 69 17 notification.
    private Reaction SetNoiseControl(ReadOnlySpan<byte> request)
    {
        if (request.Length < 7 || request[1] != NoiseControlSubtype)
        {
            return Reaction.AckOnly;
        }

        _noiseEffect = request[3];
        _noiseSettingType = request[4];
        var ambient = _noiseEffect != 0 && _noiseSettingType != 0;

        // Outside Ambient the headset holds focus on voice off.
        _focusOnVoice = ambient ? request[5] : (byte)0;
        _ambientLevel = request[6];
        return new Reaction(null, [NoiseControl(NoiseControlSet + 1)]);
    }

    // Caller holds _gate. The real headset sends nothing after an EQ SET, which is why the app re-reads the curve.
    private Reaction SetEqualizer(ReadOnlySpan<byte> request)
    {
        if (request.Length < 4)
        {
            return Reaction.AckOnly;
        }

        var preset = (EqualizerPreset)request[2];
        if (request[3] == EqualizerValueCount && request.Length >= 4 + 1 + EqualizerBandCount)
        {
            _clearBass = request[4] - EqualizerWireOffset;
            _bands = [.. request.Slice(5, EqualizerBandCount).ToArray().Select(b => b - EqualizerWireOffset)];
        }
        else if (PresetCurves.TryGetValue(preset, out var curve))
        {
            _clearBass = curve.ClearBass;
            _bands = [.. curve.Bands];
        }

        _equalizerPreset = request[2];
        return Reaction.AckOnly;
    }

    // Caller holds _gate.
    private Reaction SetDsee(ReadOnlySpan<byte> request)
    {
        if (request.Length < 3 || request[1] != DseeSubtype)
        {
            return Reaction.AckOnly;
        }

        _dsee = request[2] != 0 ? (byte)1 : (byte)0;
        return new Reaction(null, [[(byte)(DseeSet + 1), DseeSubtype, _dsee], DseeSideEffect]);
    }

    internal readonly record struct Reaction(byte[]? Reply, IReadOnlyList<byte[]> Notifications)
    {
        public static Reaction AckOnly { get; } = new(null, []);
    }
}
