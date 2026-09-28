using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Protocol.Tests.Commands;

// Init reply captured from a real WH-CH720N over the V2 RFCOMM service.
internal static class GoldenHandshake
{
    public static byte[] Reply() => [0x01, 0x00, 0x03, 0x00, 0x10, 0x02, 0x00, 0x00];

    public static V2CommandSet CreateCommands() => V2CommandSet.FromHandshake(ProtocolGeneration.V2, Reply());
}
