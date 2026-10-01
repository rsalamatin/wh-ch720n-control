using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Commands;

public sealed partial class V2CommandSet
{
    /// <summary>
    /// Applies an unsolicited payload to <paramref name="current"/>. RET opcodes are accepted too, because a reply that
    /// arrives after its request timed out is dispatched as a notification but still describes the device state.
    /// </summary>
    /// <returns>
    /// False for traffic that does not map to state: unknown opcodes or subtypes (e.g. dual/case battery), or a
    /// preset-only equalizer notification with no previously known bands.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    /// <exception cref="ProtocolFormatException">
    /// The opcode and subtype are recognised but the content is truncated or invalid.
    /// </exception>
    public bool TryApplyNotification(DeviceState current, ReadOnlySpan<byte> payload, out DeviceState updated)
    {
        ArgumentNullException.ThrowIfNull(current);
        updated = current;
        if (payload.IsEmpty)
        {
            return false;
        }

        // Unknown subtypes are skipped rather than rejected, like the reference dispatcher
        // (DeviceEventDispatcher.cpp:142, 168): devices emit subtypes this model does not describe.
        var opcode = payload[0];
        switch (opcode)
        {
            case V2Opcodes.BatteryRet or V2Opcodes.BatteryNotify:
                if (!HasSubtype(payload, V2Opcodes.BatterySingle, "battery"))
                {
                    return false;
                }

                updated = current with { Battery = ReadBattery(payload, opcode) };
                return true;

            case V2Opcodes.NoiseControlRet or V2Opcodes.NoiseControlNotify:
                if (!HasSubtype(payload, V2Opcodes.NoiseControlSubtype, "noise control")
                    || (payload.Length > 2 && payload[2] != V2Opcodes.NoiseControlVersion))
                {
                    return false;
                }

                updated = current with { NoiseControl = ReadNoiseControl(payload, opcode) };
                return true;

            case V2Opcodes.EqualizerRet or V2Opcodes.EqualizerNotify:
                return HasSubtype(payload, V2Opcodes.EqualizerSubtype, "equalizer")
                    && TryApplyEqualizer(current, payload, opcode, out updated);

            case V2Opcodes.DseeRet or V2Opcodes.DseeNotify:
                if (!HasSubtype(payload, V2Opcodes.DseeSubtype, "DSEE"))
                {
                    return false;
                }

                updated = current with { DseeEnabled = ReadDsee(payload, opcode) };
                return true;

            default:
                return false;
        }
    }

    private static bool HasSubtype(ReadOnlySpan<byte> payload, byte subtype, string what)
    {
        if (payload.Length < 2)
        {
            throw ProtocolFormatException.Malformed(what, payload, $"truncated to {payload.Length} bytes");
        }

        return payload[1] == subtype;
    }

    private static bool TryApplyEqualizer(DeviceState current, ReadOnlySpan<byte> payload, byte opcode, out DeviceState updated)
    {
        updated = current;

        // A value count of 6 announces the full table, so a short payload is truncated, not preset-only.
        var announcesValues = payload.Length > 3 && payload[3] == V2Opcodes.EqualizerValueCount;
        if (announcesValues || payload.Length >= EqualizerPayloadLength)
        {
            updated = current with { Equalizer = ReadEqualizer(payload, opcode) };
            return true;
        }

        // The reference accepts preset-only equalizer notifications and keeps the previous band values
        // (libs/sony-protocol/src/DeviceEventDispatcher.cpp:185-193).
        RequireHeader(payload, opcode, V2Opcodes.EqualizerSubtype, 3, "equalizer");
        if (current.Equalizer is null)
        {
            return false;
        }

        updated = current with { Equalizer = current.Equalizer with { Preset = (EqualizerPreset)payload[2] } };
        return true;
    }
}
