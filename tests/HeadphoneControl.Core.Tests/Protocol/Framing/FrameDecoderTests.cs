using HeadphoneControl.Protocol.Framing;

namespace HeadphoneControl.Protocol.Tests.Framing;

public class FrameDecoderTests
{
    // One read captured from a real WH-CH720N after the init handshake: ACK, DATA_MDR reply, and the same
    // reply retransmitted because the capture tool did not ACK it.
    private const string RealDeviceInitReply =
        "3E010100000000023C" + "3E0C000000000801000300100200002A3C" + "3E0C000000000801000300100200002A3C";

    // DATA_MDR seq 0, payload 22 00.
    private const string BatteryRequest = "3E0C00000000022200303C";

    [Test]
    public async Task WhenRealDeviceInitReplyIsFedThenThreeFramesAreDecoded()
    {
        var decoder = new FrameDecoder();

        var frames = decoder.Feed(Convert.FromHexString(RealDeviceInitReply));

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(
            ["1/1/", "12/0/0100030010020000", "12/0/0100030010020000"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(new byte[] { })]
    [Arguments(new byte[] { 0x00, 0x00 })]
    [Arguments(new byte[] { 0x3C, 0x3D, 0x3E })]
    [Arguments(new byte[] { 0x3D, 0x3D, 0x2C, 0x2D, 0x2E })]
    [Arguments(new byte[] { 0x2F })]
    public async Task WhenEncodedFrameIsFedThenSameFrameIsDecoded(byte[] payload)
    {
        var original = new Frame(FrameType.DataMdr, 1, payload);
        var decoder = new FrameDecoder();

        var frames = decoder.Feed(FrameCodec.Encode(original));

        await Assert.That(frames.Select(Describe)).IsEquivalentTo([Describe(original)]);
    }

    [Test]
    public async Task WhenEveryByteValueIsInPayloadThenRoundTripPreservesIt()
    {
        var original = new Frame(FrameType.DataMdr, 0, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());
        var decoder = new FrameDecoder();

        var frames = decoder.Feed(FrameCodec.Encode(original));

        await Assert.That(Describe(frames.Single())).IsEqualTo(Describe(original));
    }

    [Test]
    public async Task WhenEscapedFrameIsExactlyMaxSizeThenItIsDecoded()
    {
        var wire = FrameCodec.Encode(new Frame(FrameType.DataMdr, 0, new byte[FrameCodec.MaxEscapedFrameSize - 9]));
        var decoder = new FrameDecoder();

        var frames = decoder.Feed(wire);

        await Assert.That(frames.Single().Payload.Length).IsEqualTo(FrameCodec.MaxEscapedFrameSize - 9);
    }

    [Test]
    public async Task WhenFrameIsFedOneByteAtATimeThenExactlyOneFrameIsDecoded()
    {
        var decoder = new FrameDecoder();
        var wire = Convert.FromHexString(BatteryRequest);

        var frames = wire.SelectMany(b => decoder.Feed([b])).ToList();

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    [Test]
    public async Task WhenTwoFramesArriveInOneChunkThenBothAreDecodedInOrder()
    {
        var decoder = new FrameDecoder();
        var chunk = FrameCodec.Encode(new Frame(FrameType.DataMdr, 0, new byte[] { 0x01 }))
            .Concat(FrameCodec.Encode(new Frame(FrameType.DataMdr, 1, new byte[] { 0x02 })))
            .ToArray();

        var frames = decoder.Feed(chunk);

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(
            ["12/0/01", "12/1/02"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenChunkEndsInsideEscapePairThenFrameIsDecodedFromNextChunk()
    {
        var decoder = new FrameDecoder();
        var wire = FrameCodec.Encode(new Frame(FrameType.DataMdr, 0, new byte[] { 0x3E }));
        var escapeIndex = Array.IndexOf(wire, FrameCodec.EscapeMarker);

        var first = decoder.Feed(wire.AsSpan(0, escapeIndex + 1));
        var second = decoder.Feed(wire.AsSpan(escapeIndex + 1));

        await Assert.That(first.Concat(second).Select(Describe)).IsEquivalentTo(["12/0/3E"]);
    }

    [Test]
    public async Task WhenGarbagePrecedesStartMarkerThenFrameIsStillDecoded()
    {
        var decoder = new FrameDecoder();
        var wire = Convert.FromHexString("0102FF" + BatteryRequest);

        var frames = decoder.Feed(wire);

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    [Test]
    public async Task WhenGarbagePrecedesStartMarkerThenGarbageIsReportedAsRejected()
    {
        var decoder = new FrameDecoder();
        var rejected = new List<string>();
        decoder.FrameRejected += (_, e) => rejected.Add(Convert.ToHexString(e.RawBytes.Span));

        decoder.Feed(Convert.FromHexString("0102FF" + BatteryRequest));

        await Assert.That(rejected).IsEquivalentTo(["0102FF"]);
    }

    [Test]
    public async Task WhenSecondStartArrivesBeforeEndThenDecoderResyncsOnSecondStart()
    {
        var decoder = new FrameDecoder();
        var wire = Convert.FromHexString("3E0C0000" + BatteryRequest);

        var frames = decoder.Feed(wire);

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    [Test]
    public async Task WhenSecondStartArrivesBeforeEndThenAbandonedBytesAreRejected()
    {
        var decoder = new FrameDecoder();
        var rejected = new List<string>();
        decoder.FrameRejected += (_, e) => rejected.Add(Convert.ToHexString(e.RawBytes.Span));

        decoder.Feed(Convert.FromHexString("3E0C0000" + BatteryRequest));

        await Assert.That(rejected).IsEquivalentTo(["3E0C0000"]);
    }

    [Test]
    public async Task WhenChecksumIsWrongThenNoFrameIsDecoded()
    {
        var decoder = new FrameDecoder();

        var frames = decoder.Feed(Convert.FromHexString("3E0C00000000022200313C"));

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task WhenChecksumIsWrongThenRejectionNamesChecksum()
    {
        var decoder = new FrameDecoder();
        string? reason = null;
        decoder.FrameRejected += (_, e) => reason = e.Reason;

        decoder.Feed(Convert.FromHexString("3E0C00000000022200313C"));

        await Assert.That(reason).Contains("checksum");
    }

    [Test]
    public async Task WhenRejectedThenRawBytesAreTheWireBytes()
    {
        var decoder = new FrameDecoder();
        byte[]? raw = null;
        decoder.FrameRejected += (_, e) => raw = e.RawBytes.ToArray();

        decoder.Feed(Convert.FromHexString("3E0C00000000022200313C"));

        await Assert.That(Convert.ToHexString(raw!)).IsEqualTo("3E0C00000000022200313C");
    }

    [Test]
    public async Task WhenCorruptFrameIsFollowedByValidFrameThenOnlyValidFrameIsDecoded()
    {
        var decoder = new FrameDecoder();
        var wire = Convert.FromHexString("3E0C00000000022200313C" + BatteryRequest);

        var frames = decoder.Feed(wire);

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    [Test]
    [Arguments("3E0C00000000032200303C", "length")]
    [Arguments("3E0C00000000012200303C", "length")]
    [Arguments("3E0C000000003C", "shorter")]
    [Arguments("3E3C", "shorter")]
    [Arguments("3E0C00000000022200303D3C", "dangling")]
    [Arguments("3E0C000000000222003D11303C", "escape")]
    public async Task WhenFrameIsMalformedThenRejectionExplainsWhy(string hex, string expectedReasonPart)
    {
        var decoder = new FrameDecoder();
        string? reason = null;
        decoder.FrameRejected += (_, e) => reason = e.Reason;

        decoder.Feed(Convert.FromHexString(hex));

        await Assert.That(reason).Contains(expectedReasonPart);
    }

    [Test]
    public async Task WhenNoEndMarkerArrivesFor5000BytesThenOversizeFrameIsRejected()
    {
        var decoder = new FrameDecoder();
        var reasons = new List<string>();
        decoder.FrameRejected += (_, e) => reasons.Add(e.Reason);
        var wire = new byte[5000];
        wire[0] = FrameCodec.StartMarker;

        decoder.Feed(wire);

        await Assert.That(reasons).Contains(r => r.Contains("exceeds"));
    }

    [Test]
    public async Task WhenOversizeInputIsFedThenNoRejectedChunkExceedsMaxFrameSize()
    {
        var decoder = new FrameDecoder();
        var largest = 0;
        decoder.FrameRejected += (_, e) => largest = Math.Max(largest, e.RawBytes.Length);
        var wire = new byte[5000];
        wire[0] = FrameCodec.StartMarker;

        decoder.Feed(wire);

        await Assert.That(largest).IsLessThanOrEqualTo(FrameCodec.MaxEscapedFrameSize);
    }

    [Test]
    public async Task WhenValidFrameFollowsOversizeInputThenItIsDecoded()
    {
        var decoder = new FrameDecoder();
        var junk = new byte[5000];
        junk[0] = FrameCodec.StartMarker;
        decoder.Feed(junk);

        var frames = decoder.Feed(Convert.FromHexString(BatteryRequest));

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    [Test]
    public async Task WhenResetMidFrameThenRemainderOfThatFrameIsNotDecoded()
    {
        var decoder = new FrameDecoder();
        var wire = Convert.FromHexString(BatteryRequest);
        decoder.Feed(wire.AsSpan(0, 5));

        decoder.Reset();
        var frames = decoder.Feed(wire.AsSpan(5));

        await Assert.That(frames).IsEmpty();
    }

    [Test]
    public async Task WhenResetThenNextCompleteFrameIsDecoded()
    {
        var decoder = new FrameDecoder();
        decoder.Feed(Convert.FromHexString("3E0C0000"));

        decoder.Reset();
        var frames = decoder.Feed(Convert.FromHexString(BatteryRequest));

        await Assert.That(frames.Select(Describe)).IsEquivalentTo(["12/0/2200"]);
    }

    private static string Describe(Frame frame) =>
        $"{(int)frame.Type}/{frame.Sequence}/{Convert.ToHexString(frame.Payload.Span)}";
}
