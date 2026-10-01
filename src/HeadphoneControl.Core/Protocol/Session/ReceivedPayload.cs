namespace HeadphoneControl.Protocol.Session;

/// <summary>A received DATA_MDR payload and its place in the session's receive order.</summary>
/// <param name="Ordinal">
/// Counts every frame the session decoded, ACKs included, starting at 1. A higher ordinal was received later, so
/// it describes the device at a later moment. Ordinals are only comparable within one session.
/// </param>
public readonly record struct ReceivedPayload(ReadOnlyMemory<byte> Payload, long Ordinal);
