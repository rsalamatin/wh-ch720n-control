using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Core;

/// <summary>
/// The platform seam: lists paired Sony headsets and opens the control channel of one of them. Each platform
/// (Windows RFCOMM, later BlueZ) implements it; nothing above it knows which one is in use.
/// </summary>
public interface IHeadsetConnector
{
    /// <summary>
    /// Lists paired devices exposing a Sony control service, in <see cref="HeadsetSelection.OrderForDisplay"/> order.
    /// </summary>
    /// <exception cref="TransportException">The platform could not enumerate paired Bluetooth devices.</exception>
    /// <exception cref="TimeoutException">The platform did not finish listing them in time.</exception>
    Task<IReadOnlyList<DiscoveredHeadset>> FindPairedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens the control channel of <paramref name="headset"/>. <see cref="TransportConnection.ServiceGeneration"/>
    /// must come from the service actually connected to, never from <see cref="DiscoveredHeadset.Generation"/> or a
    /// default: opcode 0x22 is BATTERY on V2 but POWER OFF on V1.
    /// </summary>
    /// <exception cref="TransportException">The link could not be opened.</exception>
    /// <exception cref="TimeoutException">The headset did not accept the connection in time.</exception>
    Task<TransportConnection> ConnectAsync(DiscoveredHeadset headset, CancellationToken cancellationToken);
}
