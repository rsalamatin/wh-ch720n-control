using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeadphoneControl.Platform.Windows.Tests;

public class RfcommTransportTests
{
    // Not a Windows device id, so every connect attempt fails without touching real hardware.
    private const string InvalidDeviceId = "not-a-bluetooth-device-id";

    [Test]
    public async Task WhenTransportIsCreatedThenItIsNotConnected()
    {
        await using var transport = CreateTransport();

        await Assert.That(transport.IsConnected).IsFalse();
    }

    [Test]
    public async Task WhenTransportIsCreatedThenGenerationIsUnknownUntilConnected()
    {
        await using var transport = CreateTransport();

        await Assert.That(transport.DetectedGeneration).IsEqualTo(ProtocolGeneration.Unknown);
    }

    [Test]
    public async Task WhenSendingBeforeConnectThenTransportExceptionIsThrown()
    {
        await using var transport = CreateTransport();

        var act = () => transport.SendAsync(new byte[] { 0x3E }, CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenReceivingBeforeConnectThenTransportExceptionIsThrown()
    {
        await using var transport = CreateTransport();

        var act = () => transport.ReceiveAsync(new byte[16], CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenSendingAfterDisposeThenTransportExceptionIsThrown()
    {
        var transport = CreateTransport();
        await transport.DisposeAsync();

        var act = () => transport.SendAsync(new byte[] { 0x3E }, CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenTransportIsDisposedThenConnectThrowsObjectDisposedException()
    {
        var transport = CreateTransport();
        await transport.DisposeAsync();

        var act = () => transport.ConnectAsync(CancellationToken.None);

        await Assert.That(act).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task WhenDeviceIdIsInvalidThenConnectThrowsTransportException()
    {
        await using var transport = CreateTransport();

        var act = () => transport.ConnectAsync(CancellationToken.None);

        await Assert.That(act).Throws<TransportException>();
    }

    [Test]
    public async Task WhenConnectFailedThenGenerationStaysUnknown()
    {
        await using var transport = CreateTransport();
        var connect = transport.ConnectAsync(CancellationToken.None);

        await Task.WhenAny(connect);

        await Assert.That(transport.DetectedGeneration).IsEqualTo(ProtocolGeneration.Unknown);
    }

    [Test]
    public async Task WhenConnectingASecondTimeThenInvalidOperationExceptionIsThrown()
    {
        await using var transport = CreateTransport();
        await Task.WhenAny(transport.ConnectAsync(CancellationToken.None));

        var act = () => transport.ConnectAsync(CancellationToken.None);

        await Assert.That(act).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task WhenDisposedWhileConnectingThenTransportIsNotConnected()
    {
        var transport = CreateTransport();
        var connect = transport.ConnectAsync(CancellationToken.None);

        await transport.DisposeAsync();
        await Task.WhenAny(connect);

        await Assert.That(transport.IsConnected).IsFalse();
    }

    private static RfcommTransport CreateTransport() =>
        new(InvalidDeviceId, "WH-CH720N", NullLogger<RfcommTransport>.Instance);
}
