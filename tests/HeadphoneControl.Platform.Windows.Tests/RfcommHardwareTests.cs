using HeadphoneControl.Core;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeadphoneControl.Platform.Windows.Tests;

// Needs the paired WH-CH720N. Read-only: sends only the V2 init handshake.
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
        var connector = new RfcommConnector(NullLoggerFactory.Instance);

        var headset = await FindHeadsetAsync(connector);

        await Assert.That(headset.Generation).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    [Explicit]
    public async Task WhenConnectingToPairedWhCh720nThenConnectionReportsV2Service()
    {
        var connector = new RfcommConnector(NullLoggerFactory.Instance);

        var connection = await connector.ConnectAsync(await FindHeadsetAsync(connector), CancellationToken.None);
        await using var transport = connection.Transport;

        await Assert.That(connection.ServiceGeneration).IsEqualTo(ProtocolGeneration.V2);
    }

    [Test]
    [Explicit]
    public async Task WhenV2InitHandshakeIsSentThenHeadsetReplies()
    {
        var connector = new RfcommConnector(NullLoggerFactory.Instance);
        var connection = await connector.ConnectAsync(await FindHeadsetAsync(connector), CancellationToken.None);
        await using var transport = connection.Transport;

        // Never send V2 bytes unless the connected service really is V2 (0x22 means POWER OFF on V1).
        await Assert.That(connection.ServiceGeneration).IsEqualTo(ProtocolGeneration.V2);

        await transport.SendAsync(InitHandshakeFrame, CancellationToken.None);
        var reply = await CollectRepliesAsync(transport, TimeSpan.FromSeconds(3));
        Console.WriteLine($"Generation: {connection.ServiceGeneration}");
        Console.WriteLine($"Sent:  {Convert.ToHexString(InitHandshakeFrame)}");
        Console.WriteLine($"Reply: {Convert.ToHexString(reply)}");

        await Assert.That(reply.Length).IsGreaterThan(0);
    }

    private static async Task<DiscoveredHeadset> FindHeadsetAsync(RfcommConnector connector)
    {
        var headsets = await connector.FindPairedAsync(CancellationToken.None);
        Console.WriteLine($"Discovered: {string.Join(", ", headsets)}");
        return headsets.FirstOrDefault(h => h.Name.Contains(Model, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No paired {Model} with a Sony control service was found (discovered: {headsets.Count}). "
                + "Pair the headset in Windows Bluetooth settings and see docs/hardware-smoke-test.md.");
    }

    private static async Task<byte[]> CollectRepliesAsync(ITransport transport, TimeSpan window)
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
