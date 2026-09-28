using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Commands;

/// <summary>
/// The <c>[0x00, 0x00]</c> init handshake. It is the one command that is safe on every generation (opcode 0x00
/// means nothing destructive on V1 or V2), which is why it lives outside <see cref="V2CommandSet"/>.
/// </summary>
public static class ProtocolHandshake
{
    // The reference only documents the reply size ("01 ... 8 bytes total => v2 device", Client/Constants.h:35,
    // Client/Headphones.cpp:88). Length alone is too weak to unlock opcode 0x22, so byte 2 (protocol info) must
    // also match: a real WH-CH720N answers 01 00 03 00 10 02 00 00.
    private const int ReplyLength = 8;
    private const int ProtocolInfoIndex = 2;
    private const byte ProtocolInfoV1 = 0x01;
    private const byte ProtocolInfoV2 = 0x03;

    /// <summary>
    /// Builds the init request. The device answers with a DATA_MDR payload starting with opcode 0x01,
    /// so the request waits on that opcode with any subtype.
    /// </summary>
    public static MdrRequest CreateRequest() =>
        new(new byte[] { V2Opcodes.InitRequest, 0x00 }, V2Opcodes.InitReply, null);

    /// <summary>
    /// Derives the protocol generation from the init reply payload. Returns <see cref="ProtocolGeneration.V2"/>
    /// only for an 8-byte reply whose byte 2 is 0x03, <see cref="ProtocolGeneration.V1"/> for an 8-byte reply
    /// whose byte 2 is 0x01 (informational), and <see cref="ProtocolGeneration.Unknown"/> otherwise, so an
    /// unexpected reply can never unlock the V2 command set.
    /// </summary>
    /// <exception cref="ProtocolFormatException">The payload is empty or is not an init reply (opcode 0x01).</exception>
    public static ProtocolGeneration ParseGeneration(ReadOnlySpan<byte> reply)
    {
        if (reply.IsEmpty)
        {
            throw ProtocolFormatException.Malformed("init reply", reply, "empty");
        }

        if (reply[0] != V2Opcodes.InitReply)
        {
            throw ProtocolFormatException.Malformed("init reply", reply, $"opcode 0x{reply[0]:X2}, expected 0x01");
        }

        if (reply.Length != ReplyLength)
        {
            return ProtocolGeneration.Unknown;
        }

        return reply[ProtocolInfoIndex] switch
        {
            ProtocolInfoV2 => ProtocolGeneration.V2,
            ProtocolInfoV1 => ProtocolGeneration.V1,
            _ => ProtocolGeneration.Unknown,
        };
    }
}
