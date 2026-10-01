namespace HeadphoneControl.Protocol.Session;

/// <summary>A received DATA_MDR payload and its place in the session's receive order.</summary>
/// <param name="Ordinal">
/// Counts every decoded frame, ACKs included, from 1; a higher ordinal describes the device at a later moment.
/// Comparable only within one session.
/// </param>
public readonly record struct ReceivedPayload(ReadOnlyMemory<byte> Payload, long Ordinal);
