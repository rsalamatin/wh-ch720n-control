using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Resources;

namespace HeadphoneControl.Simulation;

/// <summary>
/// A paired WH-CH720N without hardware: the link speaks the real frame protocol, so framing, the session, the
/// handshake, the V2 check and the command set all run as they do against the headset.
/// </summary>
/// <remarks>
/// Settings survive a reconnect. Like the real headset, only one control channel can be open at a time.
/// </remarks>
public sealed class SimulatedHeadsetConnector : IHeadsetConnector
{
    public const string HeadsetName = "WH-CH720N (simulated)";

    private static readonly DiscoveredHeadset Headset = new(HeadsetName, "simulated", ProtocolGeneration.V2);

    private readonly SimulatedHeadset _headset = new();
    private readonly TimeSpan _latency;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private SimulatedTransport? _open;
    private int _failNextConnect;

    /// <param name="latency">Delay of the connect and of each reply; zero answers at once.</param>
    public SimulatedHeadsetConnector(TimeSpan latency, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(latency, TimeSpan.Zero);
        _latency = latency;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Makes only the next <see cref="ConnectAsync"/> fail with a <see cref="TransportException"/>.</summary>
    public bool FailNextConnect
    {
        get => Volatile.Read(ref _failNextConnect) != 0;
        set => Volatile.Write(ref _failNextConnect, value ? 1 : 0);
    }

    public Task<IReadOnlyList<DiscoveredHeadset>> FindPairedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DiscoveredHeadset>>([Headset]);
    }

    public async Task<TransportConnection> ConnectAsync(DiscoveredHeadset headset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headset);
        if (headset.DeviceId != Headset.DeviceId)
        {
            throw new TransportException($"{headset.Name} is not paired with the simulator.");
        }

        await Task.Delay(_latency, _timeProvider, cancellationToken).ConfigureAwait(false);
        if (Interlocked.Exchange(ref _failNextConnect, 0) != 0)
        {
            throw new TransportException(Strings.Get("Error_SimulatedConnectFailure"));
        }

        lock (_gate)
        {
            if (_open is not null)
            {
                throw new TransportException(Strings.Get("Error_SimulatedHeadsetBusy"));
            }

            _open = new SimulatedTransport(_headset, _latency, _timeProvider, OnClosed);
            return new TransportConnection(_open, ProtocolGeneration.V2);
        }
    }

    private void OnClosed(SimulatedTransport transport)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_open, transport))
            {
                _open = null;
            }
        }
    }
}
