using HeadphoneControl.Core;

namespace HeadphoneControl.Simulation;

/// <summary>The simulated headset is always on: it is reported as connected once, when watching starts.</summary>
public sealed class SimulatedPresenceMonitor : IHeadsetPresenceMonitor
{
    private bool _started;

    public event EventHandler<DiscoveredHeadset>? HeadsetConnected;

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("The simulated presence monitor is already started.");
        }

        _started = true;
        HeadsetConnected?.Invoke(this, SimulatedHeadsetConnector.Headset);
    }

    public void Dispose()
    {
    }
}
