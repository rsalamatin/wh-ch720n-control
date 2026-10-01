namespace HeadphoneControl.Protocol.Commands;

// Internal so no V2 opcode reaches the wire without the generation guard in V2CommandSet.
// Families follow GET = n, RET = n + 1, SET = n + 2, NTFY = n + 3 (reference: Client/Constants.h:34-51,
// libs/sony-protocol/src/DeviceEventDispatcher.cpp:140-185).
internal static class V2Opcodes
{
    internal const byte InitRequest = 0x00;
    internal const byte InitReply = 0x01;

    internal const byte FirmwareGet = 0x04;
    internal const byte FirmwareRet = 0x05;
    internal const byte FirmwareSubtype = 0x02;

    internal const byte CodecGet = 0x12;
    internal const byte CodecRet = 0x13;
    internal const byte CodecSubtype = 0x02;

    // 0x22 is POWER OFF on V1 devices. It must only ever be emitted from V2CommandSet.
    internal const byte BatteryGet = 0x22;
    internal const byte BatteryRet = 0x23;
    internal const byte BatteryNotify = 0x25;
    internal const byte BatterySingle = 0x00;

    internal const byte EqualizerGet = 0x56;
    internal const byte EqualizerRet = 0x57;
    internal const byte EqualizerSet = 0x58;
    internal const byte EqualizerNotify = 0x59;
    internal const byte EqualizerSubtype = 0x00;
    internal const byte EqualizerValueCount = 0x06;

    internal const byte NoiseControlGet = 0x66;
    internal const byte NoiseControlRet = 0x67;
    internal const byte NoiseControlSet = 0x68;
    internal const byte NoiseControlNotify = 0x69;
    internal const byte NoiseControlSubtype = 0x17;
    internal const byte NoiseControlVersion = 0x01;

    internal const byte DseeGet = 0xE6;
    internal const byte DseeRet = 0xE7;
    internal const byte DseeSet = 0xE8;
    internal const byte DseeNotify = 0xE9;
    internal const byte DseeSubtype = 0x01;
}
