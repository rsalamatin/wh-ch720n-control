using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Commands;

/// <summary>
/// Builds V2 (WH-CH720N generation) command payloads and parses their replies. Pure byte-level code: no I/O.
/// </summary>
/// <remarks>
/// The only way to obtain an instance is <see cref="FromHandshake"/>, which requires two independent V2 signals:
/// the RFCOMM service the transport connected to and the init handshake reply. This matters because the V2
/// battery query starts with opcode 0x22, which a V1 device executes as POWER OFF.
/// SET commands are ACK-only (<see cref="MdrRequest.ResponseOpcode"/> is null): the reference sends them with
/// a plain send and never waits for a RET (libs/sony-protocol/src/ProtocolV2.cpp:141, 176, 191, 215, 271).
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

    /// <summary>
    /// Returns the V2 command set only when both the transport and the init reply identify a V2 device.
    /// </summary>
    /// <param name="transportGeneration">
    /// Generation implied by the RFCOMM service UUID the transport connected to.
    /// </param>
    /// <param name="initReply">Payload of the reply to <see cref="ProtocolHandshake.CreateRequest"/>.</param>
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

    /// <summary>Single-battery query: <c>22 00</c>, answered by <c>23 00 &lt;level&gt; &lt;charging&gt;</c>.</summary>
    public MdrRequest QueryBattery() =>
        Query(V2Opcodes.BatteryGet, V2Opcodes.BatterySingle, V2Opcodes.BatteryRet, V2Opcodes.BatterySingle);

    /// <summary>Noise control query: <c>66 17</c>, answered by <c>67 17 01 ...</c>.</summary>
    public MdrRequest QueryNoiseControl() =>
        Query(V2Opcodes.NoiseControlGet, V2Opcodes.NoiseControlSubtype, V2Opcodes.NoiseControlRet, null);

    /// <summary>
    /// Noise control write: <c>68 17 01 &lt;effect&gt; &lt;settingType&gt; &lt;voice&gt; &lt;level&gt;</c>. Like the
    /// reference (ProtocolV2.cpp:126-129), which the spec was derived from, the voice flag is always sent and the
    /// level is <c>max(1, AmbientLevel)</c> in every mode, so a parsed state with level 0 round-trips.
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

    /// <summary>Equalizer query: <c>56 00</c>, answered by <c>57 00 &lt;preset&gt; 06 &lt;6 values +10&gt;</c>.</summary>
    public MdrRequest QueryEqualizer() =>
        Query(V2Opcodes.EqualizerGet, V2Opcodes.EqualizerSubtype, V2Opcodes.EqualizerRet, null);

    /// <summary>Selects a preset: <c>58 00 &lt;preset&gt; 00</c>.</summary>
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

    /// <summary>
    /// Writes manual EQ values: <c>58 00 A0 06 &lt;clearBass+10&gt; &lt;b1+10&gt; .. &lt;b5+10&gt;</c>.
    /// </summary>
    /// <param name="clearBass">Clear bass level, -10..10.</param>
    /// <param name="bands">Exactly 5 band levels, each -10..10.</param>
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

    /// <summary>DSEE query: <c>E6 01</c>, answered by <c>E7 01 &lt;enabled&gt;</c>.</summary>
    public MdrRequest QueryDsee() =>
        Query(V2Opcodes.DseeGet, V2Opcodes.DseeSubtype, V2Opcodes.DseeRet, V2Opcodes.DseeSubtype);

    /// <summary>DSEE write: <c>E8 01 &lt;0|1&gt;</c>.</summary>
    public MdrRequest SetDsee(bool enabled) =>
        Command(V2Opcodes.DseeSet, V2Opcodes.DseeSubtype, enabled ? (byte)1 : (byte)0);

    /// <summary>Firmware version query: <c>04 02</c>, answered by <c>05 02 &lt;length&gt; &lt;ASCII&gt;</c>.</summary>
    public MdrRequest QueryFirmwareVersion() =>
        Query(V2Opcodes.FirmwareGet, V2Opcodes.FirmwareSubtype, V2Opcodes.FirmwareRet, null);

    /// <summary>Active codec query: <c>12 02</c>, answered by <c>13 02 &lt;codec&gt;</c>.</summary>
    public MdrRequest QueryCodec() =>
        Query(V2Opcodes.CodecGet, V2Opcodes.CodecSubtype, V2Opcodes.CodecRet, null);

    // Response subtypes follow the reference: it only pins the subtype for battery and DSEE and accepts any
    // subtype otherwise (ProtocolV2.cpp:50-52, 97-99, 197-199, 221-223, 235-237, 249-251). The parsers still
    // check the subtype, so a mismatch surfaces as ProtocolFormatException rather than a timeout.
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
