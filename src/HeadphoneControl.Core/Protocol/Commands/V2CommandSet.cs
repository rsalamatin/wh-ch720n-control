using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Commands;

/// <summary>Builds V2 (WH-CH720N generation) command payloads and parses their replies.</summary>
/// <remarks>
/// The only way to obtain an instance is <see cref="FromHandshake"/>, which requires both the RFCOMM service and the
/// init reply to say V2, because the V2 battery query opcode 0x22 is POWER OFF on V1.
/// SET commands are ACK-only, as in the reference (libs/sony-protocol/src/ProtocolV2.cpp:141, 176, 191, 215, 271).
/// </remarks>
public sealed partial class V2CommandSet
{
    private const int MinEqualizerValue = -10;
    private const int MaxEqualizerValue = 10;
    private const int EqualizerWireOffset = 10;
    private const int EqualizerBandCount = 5;
    private const int MinAmbientLevel = 1;
    private const int MaxAmbientLevel = 20;

    private static readonly V2CommandSet Instance = new();

    private V2CommandSet()
    {
    }

    /// <exception cref="NotSupportedException">Either signal is not V2.</exception>
    /// <exception cref="ProtocolFormatException">
    /// <paramref name="initReply"/> is not an init reply (checked only when the transport is V2).
    /// </exception>
    public static V2CommandSet FromHandshake(ProtocolGeneration transportGeneration, ReadOnlySpan<byte> initReply)
    {
        if (transportGeneration != ProtocolGeneration.V2)
        {
            throw NotV2("transport", transportGeneration);
        }

        var replyGeneration = ProtocolHandshake.ParseGeneration(initReply);
        if (replyGeneration != ProtocolGeneration.V2)
        {
            throw NotV2("init reply", replyGeneration);
        }

        return Instance;
    }

    private static NotSupportedException NotV2(string source, ProtocolGeneration generation) =>
        new($"V2 commands require a confirmed V2 device; the {source} reports {generation}. " +
            "Opcode 0x22 (V2 battery) powers off V1 devices.");

    public MdrRequest QueryBattery() =>
        Query(V2Opcodes.BatteryGet, V2Opcodes.BatterySingle, V2Opcodes.BatteryRet, V2Opcodes.BatterySingle);

    public MdrRequest QueryNoiseControl() =>
        Query(V2Opcodes.NoiseControlGet, V2Opcodes.NoiseControlSubtype, V2Opcodes.NoiseControlRet, null);

    /// <summary>
    /// Like the reference (ProtocolV2.cpp:126-129), the voice flag is always sent and the level is
    /// <c>max(1, AmbientLevel)</c> in every mode, so a parsed state with level 0 round-trips.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The mode is undefined, or the ambient level is negative or above 20.
    /// </exception>
    public MdrRequest SetNoiseControl(NoiseControlState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        (byte effect, byte settingType) = state.Mode switch
        {
            NoiseControlMode.Off => ((byte)0, (byte)0),
            NoiseControlMode.NoiseCancelling => ((byte)1, (byte)0),
            NoiseControlMode.Ambient => ((byte)1, (byte)1),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state.Mode, "Unknown noise control mode."),
        };
        var voice = state.FocusOnVoice ? (byte)1 : (byte)0;
        var level = ToAmbientLevel(state.AmbientLevel, nameof(state));

        return Command(
            V2Opcodes.NoiseControlSet,
            V2Opcodes.NoiseControlSubtype,
            V2Opcodes.NoiseControlVersion,
            effect,
            settingType,
            voice,
            level);
    }

    public MdrRequest QueryEqualizer() =>
        Query(V2Opcodes.EqualizerGet, V2Opcodes.EqualizerSubtype, V2Opcodes.EqualizerRet, null);

    /// <exception cref="ArgumentException"><paramref name="preset"/> is <see cref="EqualizerPreset.Manual"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="preset"/> is not a defined preset.</exception>
    public MdrRequest SetEqualizerPreset(EqualizerPreset preset)
    {
        if (preset == EqualizerPreset.Manual)
        {
            // Manual needs band values; sending it with a zero value count leaves the device curve undefined.
            throw new ArgumentException(
                $"Use {nameof(SetEqualizerCustom)} to select the manual equalizer.", nameof(preset));
        }

        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown equalizer preset.");
        }

        return Command(V2Opcodes.EqualizerSet, V2Opcodes.EqualizerSubtype, (byte)preset, 0x00);
    }

    /// <exception cref="ArgumentNullException"><paramref name="bands"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="bands"/> does not contain exactly 5 values.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A level is outside -10..10.</exception>
    public MdrRequest SetEqualizerCustom(int clearBass, IReadOnlyList<int> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        if (bands.Count != EqualizerBandCount)
        {
            throw new ArgumentException($"Expected exactly {EqualizerBandCount} bands, got {bands.Count}.", nameof(bands));
        }

        // The reference clamps out-of-range values (ProtocolHelpers.h:22). Rejecting them instead surfaces
        // UI bugs rather than silently sending a different curve than the user asked for.
        var payload = new byte[4 + 1 + EqualizerBandCount];
        payload[0] = V2Opcodes.EqualizerSet;
        payload[1] = V2Opcodes.EqualizerSubtype;
        payload[2] = (byte)EqualizerPreset.Manual;
        payload[3] = V2Opcodes.EqualizerValueCount;
        payload[4] = ToEqualizerWire(clearBass, nameof(clearBass));
        for (var i = 0; i < EqualizerBandCount; i++)
        {
            payload[5 + i] = ToEqualizerWire(bands[i], nameof(bands));
        }

        return new MdrRequest(payload, null, null);
    }

    public MdrRequest QueryDsee() =>
        Query(V2Opcodes.DseeGet, V2Opcodes.DseeSubtype, V2Opcodes.DseeRet, V2Opcodes.DseeSubtype);

    public MdrRequest SetDsee(bool enabled) =>
        Command(V2Opcodes.DseeSet, V2Opcodes.DseeSubtype, enabled ? (byte)1 : (byte)0);

    public MdrRequest QueryFirmwareVersion() =>
        Query(V2Opcodes.FirmwareGet, V2Opcodes.FirmwareSubtype, V2Opcodes.FirmwareRet, null);

    public MdrRequest QueryCodec() =>
        Query(V2Opcodes.CodecGet, V2Opcodes.CodecSubtype, V2Opcodes.CodecRet, null);

    // Like the reference, only battery and DSEE pin the response subtype (ProtocolV2.cpp:50-52, 97-99, 197-199,
    // 221-223, 235-237, 249-251); the parsers check it, so a mismatch is a ProtocolFormatException, not a timeout.
    private static MdrRequest Query(byte opcode, byte subtype, byte responseOpcode, byte? responseSubtype) =>
        new(new[] { opcode, subtype }, responseOpcode, responseSubtype);

    private static MdrRequest Command(params byte[] payload) => new(payload, null, null);

    private static byte ToAmbientLevel(int level, string paramName)
    {
        if (level is < 0 or > MaxAmbientLevel)
        {
            throw new ArgumentOutOfRangeException(
                paramName, level, $"Ambient level must be 0..{MaxAmbientLevel}.");
        }

        // 0 means "not used" (spec 8.2); the device expects at least 1 on the wire.
        return (byte)Math.Max(MinAmbientLevel, level);
    }

    private static byte ToEqualizerWire(int value, string paramName)
    {
        if (value is < MinEqualizerValue or > MaxEqualizerValue)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"Equalizer values must be {MinEqualizerValue}..{MaxEqualizerValue}.");
        }

        return (byte)(value + EqualizerWireOffset);
    }
}
