using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;
using TUnit.Assertions.Enums;

namespace HeadphoneControl.Protocol.Tests.Commands;

public class V2CommandSetParserTests
{
    private readonly V2CommandSet _commands = GoldenHandshake.CreateCommands();

    [Test]
    public async Task WhenBatteryReplyIsSpecExampleBThenLevelIs100AndNotCharging()
    {
        byte[] payload = [0x23, 0x00, 0x64, 0x00];

        var battery = _commands.ParseBattery(payload);

        await Assert.That(battery).IsEqualTo(new BatteryState(100, false));
    }

    [Test]
    public async Task WhenBatteryReplyHasChargingFlagThenBatteryIsCharging()
    {
        byte[] payload = [0x23, 0x00, 0x32, 0x01];

        var battery = _commands.ParseBattery(payload);

        await Assert.That(battery).IsEqualTo(new BatteryState(50, true));
    }

    [Test]
    [Arguments(new byte[0])]
    [Arguments(new byte[] { 0x23, 0x00, 0x64 })]
    [Arguments(new byte[] { 0x25, 0x00, 0x64, 0x00 })]
    [Arguments(new byte[] { 0x23, 0x09, 0x64, 0x00 })]
    [Arguments(new byte[] { 0x23, 0x00, 0x65, 0x00 })]
    public async Task WhenBatteryReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseBattery(payload)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x00, 0x0C }, NoiseControlMode.Ambient, false, 12)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x01, 0x14 }, NoiseControlMode.Ambient, true, 20)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x00, 0x00, 0x01 }, NoiseControlMode.NoiseCancelling, false, 0)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x02, 0x00, 0x03 }, NoiseControlMode.Ambient, false, 3)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x00, 0x00 }, NoiseControlMode.Ambient, false, 0)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x00, 0x01, 0x00, 0x08 }, NoiseControlMode.Off, false, 8)]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x00, 0x00, 0x00, 0x01 }, NoiseControlMode.Off, false, 0)]
    public async Task WhenNoiseControlReplyIsParsedThenStateFollowsReferenceDecoding(
        byte[] payload, NoiseControlMode mode, bool focusOnVoice, int ambientLevel)
    {
        var state = _commands.ParseNoiseControl(payload);

        await Assert.That(state).IsEqualTo(new NoiseControlState(mode, focusOnVoice, ambientLevel));
    }

    [Test]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x00 })]
    [Arguments(new byte[] { 0x69, 0x17, 0x01, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0x67, 0x02, 0x01, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0x67, 0x17, 0x02, 0x01, 0x01, 0x00, 0x08 })]
    [Arguments(new byte[] { 0x67, 0x17, 0x01, 0x01, 0x01, 0x00, 0x15 })]
    public async Task WhenNoiseControlReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseNoiseControl(payload)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    public async Task WhenEqualizerReplyIsParsedThenPresetIsDecoded()
    {
        byte[] payload = [0x57, 0x00, 0x16, 0x06, 13, 10, 11, 12, 13, 14];

        var state = _commands.ParseEqualizer(payload);

        await Assert.That(state.Preset).IsEqualTo(EqualizerPreset.BassBoost);
    }

    [Test]
    public async Task WhenEqualizerReplyIsParsedThenClearBassIsWireMinusTen()
    {
        byte[] payload = [0x57, 0x00, 0x16, 0x06, 13, 10, 11, 12, 13, 14];

        var state = _commands.ParseEqualizer(payload);

        await Assert.That(state.ClearBass).IsEqualTo(3);
    }

    [Test]
    public async Task WhenEqualizerReplyIsParsedThenBandsAreWireMinusTen()
    {
        byte[] payload = [0x57, 0x00, 0x16, 0x06, 13, 0, 11, 12, 13, 20];

        var state = _commands.ParseEqualizer(payload);

        await Assert.That(state.Bands).IsEquivalentTo(new[] { -10, 1, 2, 3, 10 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenEqualizerPresetIsUnnamedThenRawWireValueIsKept()
    {
        byte[] payload = [0x57, 0x00, 0xA1, 0x06, 10, 10, 10, 10, 10, 10];

        var state = _commands.ParseEqualizer(payload);

        await Assert.That((byte)state.Preset).IsEqualTo((byte)0xA1);
    }

    [Test]
    [Arguments(new byte[] { 0x57, 0x00, 0x16, 0x06, 13, 10, 11, 12, 13 })]
    [Arguments(new byte[] { 0x57, 0x00, 0x16, 0x05, 13, 10, 11, 12, 13, 14 })]
    [Arguments(new byte[] { 0x57, 0x00, 0x16, 0x06, 21, 10, 11, 12, 13, 14 })]
    [Arguments(new byte[] { 0x57, 0x00, 0x16, 0x06, 13, 10, 11, 12, 13, 0xFF })]
    [Arguments(new byte[] { 0x57, 0x01, 0x16, 0x06, 13, 10, 11, 12, 13, 14 })]
    [Arguments(new byte[] { 0x59, 0x00, 0x16, 0x06, 13, 10, 11, 12, 13, 14 })]
    public async Task WhenEqualizerReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseEqualizer(payload)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    [Arguments(new byte[] { 0xE7, 0x01, 0x01 }, true)]
    [Arguments(new byte[] { 0xE7, 0x01, 0x02 }, true)]
    [Arguments(new byte[] { 0xE7, 0x01, 0x00 }, false)]
    public async Task WhenDseeReplyIsParsedThenNonZeroMeansEnabled(byte[] payload, bool enabled)
    {
        var result = _commands.ParseDsee(payload);

        await Assert.That(result).IsEqualTo(enabled);
    }

    [Test]
    [Arguments(new byte[] { 0xE7, 0x01 })]
    [Arguments(new byte[] { 0xE7, 0x02, 0x01 })]
    [Arguments(new byte[] { 0xE6, 0x01, 0x01 })]
    public async Task WhenDseeReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseDsee(payload)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    [Arguments(new byte[] { 0x05, 0x02, 0x05, (byte)'3', (byte)'.', (byte)'0', (byte)'.', (byte)'1' }, "3.0.1")]
    [Arguments(new byte[] { 0x05, 0x02, 0x06, (byte)'1', (byte)'.', (byte)'0', (byte)'.', (byte)'0', 0x00 }, "1.0.0")]
    [Arguments(new byte[] { 0x05, 0x02, 0x03, (byte)'2', (byte)'.', (byte)'1', 0x7A }, "2.1")]
    [Arguments(new byte[] { 0x05, 0x02, 0x00 }, "")]
    public async Task WhenFirmwareReplyIsParsedThenLengthPrefixedAsciiIsReturned(byte[] payload, string version)
    {
        var result = _commands.ParseFirmwareVersion(payload);

        await Assert.That(result).IsEqualTo(version);
    }

    [Test]
    [Arguments(new byte[] { 0x05, 0x02 })]
    [Arguments(new byte[] { 0x05, 0x02, 0x09, (byte)'1' })]
    [Arguments(new byte[] { 0x05, 0x02, 0x01, 0xC3 })]
    [Arguments(new byte[] { 0x05, 0x03, 0x01, (byte)'1' })]
    [Arguments(new byte[] { 0x04, 0x02, 0x01, (byte)'1' })]
    public async Task WhenFirmwareReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseFirmwareVersion(payload)).ThrowsExactly<ProtocolFormatException>();
    }

    [Test]
    [Arguments((byte)0x01, AudioCodec.Sbc)]
    [Arguments((byte)0x02, AudioCodec.Aac)]
    [Arguments((byte)0x10, AudioCodec.Ldac)]
    [Arguments((byte)0x20, AudioCodec.AptX)]
    [Arguments((byte)0x21, AudioCodec.AptXHd)]
    [Arguments((byte)0x99, AudioCodec.Unknown)]
    public async Task WhenCodecReplyIsParsedThenCodecByteIsMapped(byte code, AudioCodec codec)
    {
        byte[] payload = [0x13, 0x02, code];

        var result = _commands.ParseCodec(payload);

        await Assert.That(result).IsEqualTo(codec);
    }

    [Test]
    [Arguments(new byte[] { 0x13, 0x02 })]
    [Arguments(new byte[] { 0x13, 0x00, 0x01 })]
    [Arguments(new byte[] { 0x19, 0x00, 0x02 })]
    public async Task WhenCodecReplyIsMalformedThenParseThrows(byte[] payload)
    {
        await Assert.That(() => _commands.ParseCodec(payload)).ThrowsExactly<ProtocolFormatException>();
    }
}
