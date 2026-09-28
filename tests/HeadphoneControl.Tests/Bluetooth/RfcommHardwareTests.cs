using HeadphoneControl.Bluetooth;
using HeadphoneControl.Protocol.Devices;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeadphoneControl.Tests.Bluetooth;

/// <summary>
/// Needs a WH-CH720N paired with (and ideally connected to) this PC. Run explicitly, e.g.
/// <c>dotnet test --project tests/HeadphoneControl.Tests -- --treenode-filter "/*/*/RfcommHardwareTests/*"</c>.
/// Only the read-only V2 init handshake is ever sent.
/// </summary>
// The headset accepts a single RFCOMM connection to its control channel, so these must run one at a time.
[NotInParallel]
public class RfcommHardwareTests
{
    private const string Model = "WH-CH720N";

    // DATA_MDR (12), seq 0, length 2, payload [0x00, 0x00], checksum 0x0E; no byte needs escaping.
    private static readonly byte[] InitHandshakeFrame = [0x3E, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x0E, 0x3C];

    [Test]
    [Explicit]
    public async Task WhenPairedWhCh720nThenV2ServiceIsFound()
    {
        var discovery = new HeadsetDiscovery(NullLoggerFactory.Instance);

        var headset = await FindHeadsetAsync(discovery);

        await Assert.That(headset.Generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    [Explicit]
    public async Task WhenConnectingToPairedWhCh720nThenTransportDetectsV2()
    {
        var discovery = new HeadsetDiscovery(NullLoggerFactory.Instance);
        await using var transport = discovery.CreateTransport(await FindHeadsetAsync(discovery));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await transport.ConnectAsync(timeout.Token);

        await Assert.That(transport.DetectedGeneration).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    [Explicit]
    public async Task WhenV2InitHandshakeIsSentThenHeadsetReplies()
    {
        var discovery = new HeadsetDiscovery(NullLoggerFactory.Instance);
        await using var transport = discovery.CreateTransport(await FindHeadsetAsync(discovery));
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await transport.ConnectAsync(connectTimeout.Token);

        // Never send V2 bytes unless the connected service really is V2 (0x22 means POWER OFF on V1).
        await Assert.That(transport.DetectedGeneration).IsEqualTo(ProtocolGeneration.V2);

        await transport.SendAsync(InitHandshakeFrame, CancellationToken.None);
        var reply = await CollectRepliesAsync(transport, TimeSpan.FromSeconds(3));
        Console.WriteLine($"Generation: {transport.DetectedGeneration}");
        Console.WriteLine($"Sent:  {Convert.ToHexString(InitHandshakeFrame)}");
        Console.WriteLine($"Reply: {Convert.ToHexString(reply)}");

        await Assert.That(reply.Length).IsGreaterThan(0);
    }

    private static async Task<DiscoveredHeadset> FindHeadsetAsync(HeadsetDiscovery discovery)
    {
        var headsets = await discovery.FindPairedHeadsetsAsync(CancellationToken.None);
        Console.WriteLine($"Discovered: {string.Join(", ", headsets)}");
        return headsets.FirstOrDefault(h => h.Name.Contains(Model, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No paired {Model} with a Sony control service was found (discovered: {headsets.Count}). "
                + "Pair the headset in Windows Bluetooth settings and see docs/hardware-smoke-test.md.");
    }

    // Reads everything the headset sends within the window (ACK plus response, possibly notifications).
    private static async Task<byte[]> CollectRepliesAsync(RfcommTransport transport, TimeSpan window)
    {
        var received = new List<byte>();
        var buffer = new byte[2048];
        using var deadline = new CancellationTokenSource(window);
        try
        {
            while (true)
            {
                var count = await transport.ReceiveAsync(buffer, deadline.Token);
                if (count == 0)
                {
                    break;
                }

                received.AddRange(buffer.AsSpan(0, count).ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            // The window elapsed; that is the normal end of collection.
        }

        return received.ToArray();
    }
}
