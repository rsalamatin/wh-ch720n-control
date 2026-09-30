using HeadphoneControl.Protocol.Devices;

namespace HeadphoneControl.Core;

/// <summary>A paired Bluetooth device that advertises a Sony control service.</summary>
/// <param name="DeviceId">Platform device id of the Bluetooth device; pass back to <see cref="IHeadsetConnector.ConnectAsync"/>.</param>
/// <param name="Generation">
/// Generation implied by the advertised service UUID (V2 preferred when both are present). This comes from the
/// SDP cache and must be confirmed again via <c>TransportConnection.ServiceGeneration</c> after connecting.
/// </param>
public sealed record DiscoveredHeadset(string Name, string DeviceId, ProtocolGeneration Generation);
