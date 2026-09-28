namespace HeadphoneControl.Protocol.Transport;

/// <summary>
/// A connected byte stream to the headset's Sony control channel (RFCOMM on Windows).
/// Knows nothing about framing; implementations only move bytes.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>True between a successful <see cref="ConnectAsync"/> and a disconnect or dispose.</summary>
    bool IsConnected { get; }

    /// <summary>Opens the channel. Throws <see cref="TransportException"/> when the device cannot be reached.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Writes all bytes. Throws <see cref="TransportException"/> when the link is down.</summary>
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Reads whatever bytes are available (at least one, may be a partial frame or several frames).
    /// Returns 0 when the remote side closed the link.
    /// </summary>
    Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
