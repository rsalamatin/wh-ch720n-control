using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Bluetooth;

/// <summary>
/// Opens the Sony control channel of the best paired headset (WH-CH720N first) for <c>HeadphoneDevice</c>.
/// </summary>
public sealed class RfcommConnector
{
    // RfcommTransport has no timeout of its own, and with the headset off the service lookups plus the socket
    // connect can take this long before Windows reports an error.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly HeadsetDiscovery _discovery;
    private readonly ILogger<RfcommConnector> _logger;

    public RfcommConnector(HeadsetDiscovery discovery, ILogger<RfcommConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(logger);
        _discovery = discovery;
        _logger = logger;
    }

    /// <exception cref="TransportException">No paired Sony headset, or the link could not be opened.</exception>
    /// <exception cref="TimeoutException">The headset did not accept the connection in time.</exception>
    public async Task<TransportConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        RfcommTransport? transport = null;
        var connected = false;
        try
        {
            var headsets = await _discovery.FindPairedHeadsetsAsync(timeout.Token).ConfigureAwait(false);
            var headset = headsets.FirstOrDefault()
                ?? throw new TransportException("No paired Sony headset found. Pair the WH-CH720N in Windows Bluetooth settings.");

            _logger.LogInformation("Connecting to {Name} ({Generation})", headset.Name, headset.Generation);
            transport = _discovery.CreateTransport(headset);
            await transport.ConnectAsync(timeout.Token).ConfigureAwait(false);
            connected = true;
            return new TransportConnection(transport, transport.DetectedGeneration);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The headset did not accept the connection within {ConnectTimeout.TotalSeconds:0} s.", ex);
        }
        finally
        {
            if (!connected && transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
