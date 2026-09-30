using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;

namespace HeadphoneControl.Core;

/// <summary>Which of the paired headsets to show first and to connect to.</summary>
public static class HeadsetSelection
{
    private const string PreferredModel = "WH-CH720N";

    /// <summary>
    /// Drops devices without a Sony control service and orders the rest for display: WH-CH720N first, then V2
    /// before V1, then by name. Other Sony models are kept so the user can still pick them.
    /// </summary>
    public static IReadOnlyList<DiscoveredHeadset> OrderForDisplay(IEnumerable<DiscoveredHeadset> headsets)
    {
        ArgumentNullException.ThrowIfNull(headsets);
        return headsets
            .Where(h => h.Generation != ProtocolGeneration.Unknown)
            .OrderByDescending(h => h.Name.Contains(PreferredModel, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(h => h.Generation == ProtocolGeneration.V2)
            .ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(h => h.DeviceId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Connects to the first paired headset in display order.</summary>
    /// <exception cref="TransportException">No paired Sony headset, or the link could not be opened.</exception>
    /// <exception cref="TimeoutException">The headset did not accept the connection in time.</exception>
    public static async Task<TransportConnection> ConnectPreferredAsync(
        this IHeadsetConnector connector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var headsets = await connector.FindPairedAsync(cancellationToken).ConfigureAwait(false);
        var headset = OrderForDisplay(headsets).FirstOrDefault()
            ?? throw new TransportException("No paired Sony headset found. Pair the WH-CH720N in Windows Bluetooth settings.");
        return await connector.ConnectAsync(headset, cancellationToken).ConfigureAwait(false);
    }
}
