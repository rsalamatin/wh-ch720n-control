using System.Text;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Commands;

public sealed partial class V2CommandSet
{
    private const int MaxBatteryLevel = 100;
    private const int EqualizerPayloadLength = 10;
    private const int NoiseControlPayloadLength = 7;
    private const byte AsciiMax = 0x7F;

    /// <summary>Parses <c>23 00 &lt;level&gt; &lt;charging&gt;</c>.</summary>
    /// <exception cref="ProtocolFormatException">Wrong opcode/subtype, truncated, or level above 100.</exception>
    public BatteryState ParseBattery(ReadOnlySpan<byte> payload) => ReadBattery(payload, V2Opcodes.BatteryRet);

    /// <summary>
    /// Parses <c>67 17 01 &lt;effect&gt; &lt;settingType&gt; &lt;voice&gt; &lt;level&gt;</c>. Effect 0 is off, otherwise
    /// a non-zero settingType is ambient and 0 is noise cancelling. The level is reported as 0 when settingType
    /// is noise cancelling.
    /// </summary>
    /// <exception cref="ProtocolFormatException">
    /// Wrong opcode/subtype/version byte, truncated, or a level above 20.
    /// </exception>
    public NoiseControlState ParseNoiseControl(ReadOnlySpan<byte> payload) =>
        ReadNoiseControl(payload, V2Opcodes.NoiseControlRet);

    /// <summary>
    /// Parses <c>57 00 &lt;preset&gt; 06 &lt;clearBass+10&gt; &lt;b1+10&gt; .. &lt;b5+10&gt;</c>. A preset byte that is
    /// not a named <see cref="EqualizerPreset"/> member is passed through as its raw wire value.
    /// </summary>
    /// <exception cref="ProtocolFormatException">
    /// Wrong opcode/subtype, truncated, value count other than 6, or a value outside -10..10.
    /// </exception>
    public EqualizerState ParseEqualizer(ReadOnlySpan<byte> payload) =>
        ReadEqualizer(payload, V2Opcodes.EqualizerRet);

    /// <summary>Parses <c>E7 01 &lt;enabled&gt;</c>; any non-zero value means enabled.</summary>
    /// <exception cref="ProtocolFormatException">Wrong opcode/subtype or truncated.</exception>
    public bool ParseDsee(ReadOnlySpan<byte> payload) => ReadDsee(payload, V2Opcodes.DseeRet);

    /// <summary>
    /// Parses <c>05 02 &lt;length&gt; &lt;ASCII version&gt;</c>. Trailing NUL padding is removed.
    /// </summary>
    /// <exception cref="ProtocolFormatException">
    /// Wrong opcode/subtype, the declared length exceeds the payload, or the text is not ASCII.
    /// </exception>
    public string ParseFirmwareVersion(ReadOnlySpan<byte> payload)
    {
        const string What = "firmware version";
        RequireHeader(payload, V2Opcodes.FirmwareRet, V2Opcodes.FirmwareSubtype, 3, What);

        // The spec says the text starts "immediately after the fixed header", but byte 2 is a length prefix:
        // the reference skips it (ProtocolV2.cpp:227) and its test feeds 05 02 05 '3' '.' '0' '.' '1'
        // (tests/protocol/ProtocolV1Tests.cpp:207).
        int length = payload[2];
        if (3 + length > payload.Length)
        {
            throw ProtocolFormatException.Malformed(What, payload, $"declared length {length} exceeds payload");
        }

        var text = payload.Slice(3, length);
        foreach (var b in text)
        {
            if (b > AsciiMax)
            {
                throw ProtocolFormatException.Malformed(What, payload, $"non-ASCII byte 0x{b:X2}");
            }
        }

        return Encoding.ASCII.GetString(text).TrimEnd('\0');
    }

    /// <summary>
    /// Parses <c>13 02 &lt;codec&gt;</c>. Codec bytes the reference does not know (including LC3, whose code is
    /// undocumented) map to <see cref="AudioCodec.Unknown"/> rather than failing.
    /// </summary>
    /// <exception cref="ProtocolFormatException">Wrong opcode/subtype or truncated.</exception>
    public AudioCodec ParseCodec(ReadOnlySpan<byte> payload)
    {
        RequireHeader(payload, V2Opcodes.CodecRet, V2Opcodes.CodecSubtype, 3, "codec");

        // Codes from libs/sony-protocol/src/ProtocolHelpers.h:11-19.
        return payload[2] switch
        {
            0x01 => AudioCodec.Sbc,
            0x02 => AudioCodec.Aac,
            0x10 => AudioCodec.Ldac,
            0x20 => AudioCodec.AptX,
            0x21 => AudioCodec.AptXHd,
            _ => AudioCodec.Unknown,
        };
    }

    private static BatteryState ReadBattery(ReadOnlySpan<byte> payload, byte opcode)
    {
        const string What = "battery";
        RequireHeader(payload, opcode, V2Opcodes.BatterySingle, 4, What);

        int level = payload[2];
        if (level > MaxBatteryLevel)
        {
            throw ProtocolFormatException.Malformed(What, payload, $"level {level} exceeds {MaxBatteryLevel}");
        }

        // Only 1 means charging, matching ProtocolV2.cpp:57.
        return new BatteryState(level, payload[3] == 1);
    }

    private static NoiseControlState ReadNoiseControl(ReadOnlySpan<byte> payload, byte opcode)
    {
        const string What = "noise control";
        RequireHeader(payload, opcode, V2Opcodes.NoiseControlSubtype, NoiseControlPayloadLength, What);
        if (payload[2] != V2Opcodes.NoiseControlVersion)
        {
            throw ProtocolFormatException.Malformed(What, payload, $"byte 2 is 0x{payload[2]:X2}, expected 0x01");
        }

        // A level above 20 would produce a state SetNoiseControl rejects, so it is treated as malformed.
        int level = payload[6];
        if (level > MaxAmbientLevel)
        {
            throw ProtocolFormatException.Malformed(What, payload, $"ambient level {level} exceeds {MaxAmbientLevel}");
        }

        // Any non-zero setting type is ambient, as in the reference (ProtocolV2.cpp:108, 114).
        var ambient = payload[4] != 0;
        var mode = payload[3] == 0
            ? NoiseControlMode.Off
            : ambient ? NoiseControlMode.Ambient : NoiseControlMode.NoiseCancelling;
        return new NoiseControlState(mode, payload[5] != 0, ambient ? level : 0);
    }

    private static EqualizerState ReadEqualizer(ReadOnlySpan<byte> payload, byte opcode)
    {
        const string What = "equalizer";
        RequireHeader(payload, opcode, V2Opcodes.EqualizerSubtype, EqualizerPayloadLength, What);
        if (payload[3] != V2Opcodes.EqualizerValueCount)
        {
            throw ProtocolFormatException.Malformed(What, payload, $"value count {payload[3]}, expected 6");
        }

        var clearBass = FromEqualizerWire(payload, 4);
        var bands = new int[EqualizerBandCount];
        for (var i = 0; i < EqualizerBandCount; i++)
        {
            bands[i] = FromEqualizerWire(payload, 5 + i);
        }

        // Raw cast on purpose: some Sony firmwares report custom slots (e.g. 0xA1) that the enum does not name,
        // and failing the whole read over that would hide the band values the UI can still show.
        return new EqualizerState((EqualizerPreset)payload[2], clearBass, bands);
    }

    private static bool ReadDsee(ReadOnlySpan<byte> payload, byte opcode)
    {
        RequireHeader(payload, opcode, V2Opcodes.DseeSubtype, 3, "DSEE");
        return payload[2] != 0;
    }

    private static int FromEqualizerWire(ReadOnlySpan<byte> payload, int index)
    {
        var value = payload[index] - EqualizerWireOffset;
        if (value is < MinEqualizerValue or > MaxEqualizerValue)
        {
            throw ProtocolFormatException.Malformed(
                "equalizer", payload, $"byte {index} decodes to {value}, outside -10..10");
        }

        return value;
    }

    private static void RequireHeader(ReadOnlySpan<byte> payload, byte opcode, byte subtype, int minLength, string what)
    {
        if (payload.Length < 2)
        {
            throw ProtocolFormatException.Malformed(what, payload, $"truncated to {payload.Length} bytes");
        }

        if (payload[0] != opcode)
        {
            throw ProtocolFormatException.Malformed(what, payload, $"opcode 0x{payload[0]:X2}, expected 0x{opcode:X2}");
        }

        if (payload[1] != subtype)
        {
            throw ProtocolFormatException.Malformed(what, payload, $"subtype 0x{payload[1]:X2}, expected 0x{subtype:X2}");
        }

        if (payload.Length < minLength)
        {
            throw ProtocolFormatException.Malformed(
                what, payload, $"truncated to {payload.Length} bytes, expected at least {minLength}");
        }
    }
}
