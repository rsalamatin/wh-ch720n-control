using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Core;

/// <summary>
/// The platform seam that notices a paired Sony headset joining this computer over Bluetooth, e.g. switched on or
/// reconnected for audio. It only watches; opening the control channel is <see cref="IHeadsetConnector"/>'s job.
/// </summary>
public interface IHeadsetPresenceMonitor : IDisposable
{
    /// <summary>
    /// Raised on any thread each time a headset's Bluetooth link to this computer comes up, and once after
    /// <see cref="Start"/> for a headset that is already connected.
    /// </summary>
    event EventHandler<DiscoveredHeadset>? HeadsetConnected;

    /// <summary>Starts watching. Subscribe first: a headset that is already connected is reported right away.</summary>
    /// <exception cref="TransportException">The platform could not watch Bluetooth devices.</exception>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    /// <exception cref="ObjectDisposedException">The monitor was disposed.</exception>
    void Start();
}
