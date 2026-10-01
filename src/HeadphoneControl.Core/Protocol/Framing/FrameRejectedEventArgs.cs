namespace HeadphoneControl.Protocol.Framing;

public sealed class FrameRejectedEventArgs : EventArgs
{
    public FrameRejectedEventArgs(string reason, ReadOnlyMemory<byte> rawBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Reason = reason;
        RawBytes = rawBytes;
    }

    public string Reason { get; }

    /// <summary>The discarded bytes as received on the wire (still escaped).</summary>
    public ReadOnlyMemory<byte> RawBytes { get; }
}
