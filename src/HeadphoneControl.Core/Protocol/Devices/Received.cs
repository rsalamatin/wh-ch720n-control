namespace HeadphoneControl.Protocol.Devices;

// Ordinal is the ReceivedPayload.Ordinal of the reply that carried Value.
internal readonly record struct Received<T>(T Value, long Ordinal);
