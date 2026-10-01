namespace HeadphoneControl.Protocol.Devices;

/// <summary>
/// A value read from the device, with the <see cref="Session.ReceivedPayload.Ordinal">receive ordinal</see> of the
/// reply that carried it.
/// </summary>
public readonly record struct Received<T>(T Value, long Ordinal);
