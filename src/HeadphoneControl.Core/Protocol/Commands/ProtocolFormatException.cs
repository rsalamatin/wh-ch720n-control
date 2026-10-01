namespace HeadphoneControl.Protocol.Commands;

/// <summary>A reply or notification payload is truncated, has the wrong opcode/subtype, or holds undefined values.</summary>
public sealed class ProtocolFormatException : FormatException
{
    public ProtocolFormatException()
    {
    }

    public ProtocolFormatException(string message)
        : base(message)
    {
    }

    public ProtocolFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal static ProtocolFormatException Malformed(string what, ReadOnlySpan<byte> payload, string reason) =>
        new($"Malformed {what} payload ({reason}): [{Convert.ToHexString(payload)}]");
}
