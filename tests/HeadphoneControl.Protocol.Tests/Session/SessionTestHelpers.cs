using HeadphoneControl.Protocol.Framing;
using HeadphoneControl.Protocol.Session;
using HeadphoneControl.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeadphoneControl.Protocol.Tests.Session;

internal static class SessionTestHelpers
{
    // Upper bound for anything a test awaits, so a broken session fails the test instead of hanging it.
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public static async Task<ProtocolSession> StartSessionAsync(FakeTransport transport, TimeProvider? timeProvider = null)
    {
        var session = new ProtocolSession(transport, NullLogger<ProtocolSession>.Instance, timeProvider);
        await session.StartAsync(CancellationToken.None);
        return session;
    }

    public static Frame Data(byte sequence, params byte[] payload) => new(FrameType.DataMdr, sequence, payload);

    public static Frame Ack(byte sequence) => new(FrameType.Ack, sequence, ReadOnlyMemory<byte>.Empty);

    public static string Hex(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(bytes.Span);

    public static string Describe(Frame frame) => $"{(int)frame.Type}/{frame.Sequence}/{Hex(frame.Payload)}";

    public static CancellationToken PatienceToken() => new CancellationTokenSource(Patience).Token;

    public static Task<string> NextNotificationAsync(ProtocolSession session, Func<Frame, bool>? predicate = null)
    {
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.NotificationReceived += (_, frame) =>
        {
            if (predicate is null || predicate(frame))
            {
                received.TrySetResult(Hex(frame.Payload));
            }
        };
        return received.Task.WaitAsync(Patience);
    }
}
