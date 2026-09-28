using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Session;

internal static partial class SessionLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "TX {Hex}")]
    public static partial void Transmitted(ILogger logger, string hex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "RX {Hex}")]
    public static partial void Received(ILogger logger, string hex);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Rejected incoming bytes ({Reason}): {Hex}")]
    public static partial void FrameRejected(ILogger logger, string reason, string hex);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug, Message = "Duplicate DATA_MDR seq={Sequence} ACKed and dropped")]
    public static partial void DuplicateDropped(ILogger logger, byte sequence);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug, Message = "ACKed but not dispatched: frame type {Type} seq={Sequence}")]
    public static partial void FrameIgnored(ILogger logger, byte type, byte sequence);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Link to the device was lost")]
    public static partial void LinkLost(ILogger logger, Exception cause);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "A {EventName} handler threw; other handlers and the session are unaffected")]
    public static partial void HandlerFailed(ILogger logger, string eventName, Exception exception);

    [LoggerMessage(EventId = 8, Level = LogLevel.Debug, Message = "ACK seq={Sequence} does not match the frame in flight; ignored")]
    public static partial void LateAckIgnored(ILogger logger, byte sequence);
}
