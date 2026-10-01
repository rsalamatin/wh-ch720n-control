namespace HeadphoneControl.Protocol.Transport;

/// <summary>The underlying Bluetooth link failed, was refused, or dropped.</summary>
public sealed class TransportException : IOException
{
    public TransportException(string message)
        : base(message)
    {
    }

    public TransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
