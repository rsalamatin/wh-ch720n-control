using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Platform.Windows;

/// <summary>
/// The Windows <see cref="IHeadsetConnector"/>: lists paired headsets from the SDP cache and opens the Sony RFCOMM
/// control channel through WinRT.
/// </summary>
public sealed class RfcommConnector : IHeadsetConnector
{
    // RfcommTransport has no timeout of its own, and with the headset off the service lookups plus the socket
    // connect can take this long before Windows reports an error. The budget bounds each call separately, so
    // HeadsetSelection.ConnectPreferredAsync (list, then connect) can take up to two budgets.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RfcommConnector> _logger;
    private readonly HeadsetDiscovery _discovery;

    public RfcommConnector(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RfcommConnector>();
        _discovery = new HeadsetDiscovery(loggerFactory.CreateLogger<HeadsetDiscovery>());
    }

    /// <remarks>Uses the SDP cache, so headsets that are paired but currently off are listed too.</remarks>
    public Task<IReadOnlyList<DiscoveredHeadset>> FindPairedAsync(CancellationToken cancellationToken) =>
        WithTimeoutAsync(
            _discovery.FindPairedHeadsetsAsync, "Windows did not list the paired Bluetooth devices", cancellationToken);

    public Task<TransportConnection> ConnectAsync(DiscoveredHeadset headset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headset);
        return WithTimeoutAsync(
            async ct =>
            {
                _logger.LogInformation("Connecting to {Name} ({Generation})", headset.Name, headset.Generation);
                var transport = new RfcommTransport(
                    headset.DeviceId, headset.Name, _loggerFactory.CreateLogger<RfcommTransport>());
                try
                {
                    await transport.ConnectAsync(ct).ConfigureAwait(false);
                    return new TransportConnection(transport, transport.DetectedGeneration);
                }
                catch
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            "The headset did not accept the connection",
            cancellationToken);
    }

    private static async Task<T> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation, string timeoutMessage, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            return await operation(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"{timeoutMessage} within {ConnectTimeout.TotalSeconds:0} s.", ex);
        }
    }
}
