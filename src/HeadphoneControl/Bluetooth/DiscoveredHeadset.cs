using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Bluetooth;

/// <summary>A paired Bluetooth device that advertises a Sony control service.</summary>
/// <param name="DeviceId">Windows device id of the Bluetooth device; pass back to <see cref="HeadsetDiscovery.CreateTransport"/>.</param>
/// <param name="Generation">
/// Generation implied by the advertised service UUID (V2 preferred when both are present). This comes from the
/// SDP cache and must be confirmed again via <see cref="RfcommTransport.DetectedGeneration"/> after connecting.
/// </param>
public sealed record DiscoveredHeadset(string Name, string DeviceId, ProtocolGeneration Generation);
