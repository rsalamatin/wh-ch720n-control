using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Core;

/// <param name="Generation">
/// From the SDP cache (V2 preferred when both are advertised); must be confirmed by
/// <see cref="TransportConnection.ServiceGeneration"/> after connecting.
/// </param>
public sealed record DiscoveredHeadset(string Name, string DeviceId, ProtocolGeneration Generation);
