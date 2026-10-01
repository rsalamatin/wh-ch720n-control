namespace HeadphoneControl.Protocol.Transport;

/// <summary>
/// A connected byte stream to the headset's Sony control channel (RFCOMM on Windows), handed out already
/// connected; disposing it closes the link. Knows nothing about framing.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Writes all bytes. Throws <see cref="TransportException"/> when the link is down.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads whatever bytes are available (at least one; may be a partial frame or several frames).
    /// Returns 0 when the remote side closed the link.
    /// </summary>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
