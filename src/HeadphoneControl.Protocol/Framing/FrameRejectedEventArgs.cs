namespace HeadphoneControl.Protocol.Framing;

/// <summary>Describes wire bytes the decoder discarded, for hex diagnostics.</summary>
public sealed class FrameRejectedEventArgs : EventArgs
{
    public FrameRejectedEventArgs(string reason, ReadOnlyMemory<byte> rawBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
        RawBytes = rawBytes;
    }

    /// <summary>Why the bytes were rejected (e.g. "checksum mismatch").</summary>
    public string Reason { get; }

    /// <summary>The discarded bytes as received on the wire.</summary>
    public ReadOnlyMemory<byte> RawBytes { get; }
}
