namespace HeadphoneControl.Protocol.Commands;

/// <summary>
/// Thrown when a response or notification payload is truncated, carries the wrong opcode/subtype, or holds
/// values outside what the protocol defines. The message includes the payload in hex for diagnostics.
/// </summary>
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
