using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Bluetooth;

/// <summary>
/// RFCOMM service class UUIDs of the Sony control channel. Which one a device advertises tells the
/// protocol generation, which matters because opcode 0x22 is BATTERY on V2 but POWER OFF on V1.
/// </summary>
public static class SonyServiceIds
{
    /// <summary>Service UUID of V2 devices (WH-CH720N and newer).</summary>
    public static Guid V2 { get; } = new("956C7B26-D49A-4BA8-B03F-B17D393CB6E2");

    public static Guid V1 { get; } = new("96CC203E-5068-46ad-B32D-E316F5E069BA");

    /// <returns><see cref="ProtocolGeneration.Unknown"/> when the UUID is not a Sony control service.</returns>
    public static ProtocolGeneration GetGeneration(Guid serviceUuid) =>
        serviceUuid == V2 ? ProtocolGeneration.V2
        : serviceUuid == V1 ? ProtocolGeneration.V1
        : ProtocolGeneration.Unknown;
}
