using HeadphoneControl.Protocol.Framing;

namespace HeadphoneControl.Protocol.Tests.Framing;

public class FrameCodecTests
{
    [Test]
    public async Task WhenInitHandshakeIsEncodedThenWireBytesMatchRealDeviceCapture()
    {
        var frame = new Frame(FrameType.DataMdr, 0, new byte[] { 0x00, 0x00 });

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire)).IsEqualTo("3E0C000000000200000E3C");
    }

    [Test]
    public async Task WhenBatteryRequestIsEncodedThenChecksumIsSumOfTypeThroughPayload()
    {
        var frame = new Frame(FrameType.DataMdr, 1, new byte[] { 0x22, 0x00 });

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire)).IsEqualTo("3E0C01000000022200313C");
    }

    [Test]
    public async Task WhenAckIsEncodedThenLengthIsZeroAndPayloadIsEmpty()
    {
        var frame = new Frame(FrameType.Ack, 1, ReadOnlyMemory<byte>.Empty);

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire)).IsEqualTo("3E010100000000023C");
    }

    [Test]
    public async Task WhenPayloadContainsReservedBytesThenEachIsEscapedAsMarkerMinus0x10()
    {
        var frame = new Frame(FrameType.DataMdr, 0, new byte[] { 0x3C, 0x3D, 0x3E });

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire)).IsEqualTo("3E0C00000000033D2C3D2D3D2EC63C");
    }

    [Test]
    public async Task WhenChecksumIsAReservedByteThenChecksumIsEscaped()
    {
        // 0x0C + 0x01 + 0x2F = 0x3C
        var frame = new Frame(FrameType.DataMdr, 0, new byte[] { 0x2F });

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire)).IsEqualTo("3E0C00000000012F3D2C3C");
    }

    [Test]
    public async Task WhenPayloadIsLongerThan255ThenLengthIsBigEndian()
    {
        var frame = new Frame(FrameType.DataMdr, 0, new byte[300]);

        var wire = FrameCodec.Encode(frame);

        await Assert.That(Convert.ToHexString(wire, 3, 4)).IsEqualTo("0000012C");
    }

    [Test]
    public async Task WhenEscapedFrameIsExactlyMaxSizeThenItIsEncoded()
    {
        // 2 markers + 6 header + 1 checksum + 2039 payload = 2048
        var frame = new Frame(FrameType.DataMdr, 0, new byte[FrameCodec.MaxEscapedFrameSize - 9]);

        var wire = FrameCodec.Encode(frame);

        await Assert.That(wire.Length).IsEqualTo(FrameCodec.MaxEscapedFrameSize);
    }

    [Test]
    public async Task WhenUnescapedFrameExceedsMaxSizeThenArgumentExceptionIsThrown()
    {
        var frame = new Frame(FrameType.DataMdr, 0, new byte[FrameCodec.MaxEscapedFrameSize - 8]);

        var act = () => FrameCodec.Encode(frame);

        await Assert.That(act).Throws<ArgumentException>();
    }

    [Test]
    public async Task WhenEscapingPushesFrameOverMaxSizeThenArgumentExceptionIsThrown()
    {
        var payload = Enumerable.Repeat(FrameCodec.StartMarker, 1100).ToArray();
        var frame = new Frame(FrameType.DataMdr, 0, payload);

        var act = () => FrameCodec.Encode(frame);

        await Assert.That(act).Throws<ArgumentException>();
    }

    [Test]
    public async Task WhenFrameIsNullThenArgumentNullExceptionIsThrown()
    {
        Frame frame = null!;

        var act = () => FrameCodec.Encode(frame);

        await Assert.That(act).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WhenEncodedThenNoReservedByteAppearsBetweenMarkersExceptEscape()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var frame = new Frame(FrameType.DataMdr, 0, payload);

        var wire = FrameCodec.Encode(frame);

        var inner = wire[1..^1];
        await Assert.That(inner.Count(b => b is FrameCodec.StartMarker or FrameCodec.EndMarker)).IsEqualTo(0);
    }
}
