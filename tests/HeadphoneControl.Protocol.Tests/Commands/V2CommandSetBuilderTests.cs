using HeadphoneControl.Protocol.Commands;
using HeadphoneControl.Protocol.Devices;
using TUnit.Assertions.Enums;

namespace HeadphoneControl.Protocol.Tests.Commands;

public class V2CommandSetBuilderTests
{
    private readonly V2CommandSet _commands = GoldenHandshake.CreateCommands();

    public static IEnumerable<Func<(Func<V2CommandSet, MdrRequest> Build, byte[] Payload)>> QueryPayloads()
    {
        yield return () => (c => c.QueryBattery(), [0x22, 0x00]);
        yield return () => (c => c.QueryNoiseControl(), [0x66, 0x17]);
        yield return () => (c => c.QueryEqualizer(), [0x56, 0x00]);
        yield return () => (c => c.QueryDsee(), [0xE6, 0x01]);
        yield return () => (c => c.QueryFirmwareVersion(), [0x04, 0x02]);
        yield return () => (c => c.QueryCodec(), [0x12, 0x02]);
    }

    public static IEnumerable<Func<(Func<V2CommandSet, MdrRequest> Build, byte Opcode, byte? Subtype)>> QueryResponseMatchers()
    {
        yield return () => (c => c.QueryBattery(), 0x23, 0x00);
        yield return () => (c => c.QueryNoiseControl(), 0x67, null);
        yield return () => (c => c.QueryEqualizer(), 0x57, null);
        yield return () => (c => c.QueryDsee(), 0xE7, 0x01);
        yield return () => (c => c.QueryFirmwareVersion(), 0x05, null);
        yield return () => (c => c.QueryCodec(), 0x13, null);
    }

    public static IEnumerable<Func<Func<V2CommandSet, MdrRequest>>> SetCommands()
    {
        yield return () => c => c.SetNoiseControl(new NoiseControlState(NoiseControlMode.Ambient, false, 8));
        yield return () => c => c.SetEqualizerPreset(EqualizerPreset.BassBoost);
        yield return () => c => c.SetEqualizerCustom(0, [0, 0, 0, 0, 0]);
        yield return () => c => c.SetDsee(true);
    }

    [Test]
    [MethodDataSource(nameof(QueryPayloads))]
    public async Task WhenQueryIsBuiltThenPayloadMatchesSpec(Func<V2CommandSet, MdrRequest> build, byte[] payload)
    {
        var request = build(_commands);

        await Assert.That(request.Payload.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(QueryResponseMatchers))]
    public async Task WhenQueryIsBuiltThenItAwaitsTheRetOpcode(Func<V2CommandSet, MdrRequest> build, byte opcode, byte? subtype)
    {
        var request = build(_commands);

        await Assert.That((request.ResponseOpcode, request.ResponseSubtype)).IsEqualTo(((byte?)opcode, subtype));
    }

    [Test]
    [MethodDataSource(nameof(SetCommands))]
    public async Task WhenSetCommandIsBuiltThenItIsAckOnly(Func<V2CommandSet, MdrRequest> build)
    {
        var request = build(_commands);

        await Assert.That(request.ResponseOpcode).IsNull();
    }

    [Test]
    public async Task WhenAmbientLevel8IsSetThenPayloadIsSpecExampleC()
    {
        var request = _commands.SetNoiseControl(new NoiseControlState(NoiseControlMode.Ambient, false, 8));

        await Assert.That(request.Payload.ToArray())
            .IsEquivalentTo(new byte[] { 0x68, 0x17, 0x01, 0x01, 0x01, 0x00, 0x08 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(NoiseControlMode.Off, false, 0, new byte[] { 0x68, 0x17, 0x01, 0x00, 0x00, 0x00, 0x01 })]
    [Arguments(NoiseControlMode.Off, true, 12, new byte[] { 0x68, 0x17, 0x01, 0x00, 0x00, 0x01, 0x0C })]
    [Arguments(NoiseControlMode.NoiseCancelling, false, 0, new byte[] { 0x68, 0x17, 0x01, 0x01, 0x00, 0x00, 0x01 })]
    [Arguments(NoiseControlMode.NoiseCancelling, true, 12, new byte[] { 0x68, 0x17, 0x01, 0x01, 0x00, 0x01, 0x0C })]
    [Arguments(NoiseControlMode.Ambient, false, 0, new byte[] { 0x68, 0x17, 0x01, 0x01, 0x01, 0x00, 0x01 })]
    [Arguments(NoiseControlMode.Ambient, false, 1, new byte[] { 0x68, 0x17, 0x01, 0x01, 0x01, 0x00, 0x01 })]
    [Arguments(NoiseControlMode.Ambient, true, 20, new byte[] { 0x68, 0x17, 0x01, 0x01, 0x01, 0x01, 0x14 })]
    public async Task WhenNoiseControlIsSetThenPayloadFollowsReferenceMapping(
        NoiseControlMode mode, bool focusOnVoice, int ambientLevel, byte[] payload)
    {
        var request = _commands.SetNoiseControl(new NoiseControlState(mode, focusOnVoice, ambientLevel));

        await Assert.That(request.Payload.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(NoiseControlMode.Ambient, 21)]
    [Arguments(NoiseControlMode.Ambient, -1)]
    [Arguments(NoiseControlMode.NoiseCancelling, 21)]
    [Arguments(NoiseControlMode.Off, -1)]
    public async Task WhenAmbientLevelIsOutOfRangeThenSetNoiseControlThrows(NoiseControlMode mode, int ambientLevel)
    {
        var state = new NoiseControlState(mode, false, ambientLevel);

        await Assert.That(() => _commands.SetNoiseControl(state)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenParsedNoiseCancellingStateIsSwitchedToAmbientThenItRoundTripsWithLevelOne()
    {
        byte[] reply = [0x67, 0x17, 0x01, 0x01, 0x00, 0x00, 0x05];
        var parsed = _commands.ParseNoiseControl(reply);

        var request = _commands.SetNoiseControl(parsed with { Mode = NoiseControlMode.Ambient });

        await Assert.That(request.Payload.ToArray())
            .IsEquivalentTo(new byte[] { 0x68, 0x17, 0x01, 0x01, 0x01, 0x00, 0x01 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenNoiseControlModeIsUndefinedThenSetNoiseControlThrows()
    {
        var state = new NoiseControlState((NoiseControlMode)42, false, 1);

        await Assert.That(() => _commands.SetNoiseControl(state)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenNoiseControlStateIsNullThenSetNoiseControlThrows()
    {
        await Assert.That(() => _commands.SetNoiseControl(null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    [Arguments(EqualizerPreset.Off, (byte)0x00)]
    [Arguments(EqualizerPreset.Bright, (byte)0x10)]
    [Arguments(EqualizerPreset.BassBoost, (byte)0x16)]
    [Arguments(EqualizerPreset.Speech, (byte)0x17)]
    public async Task WhenEqualizerPresetIsSetThenPayloadCarriesPresetId(EqualizerPreset preset, byte presetId)
    {
        var request = _commands.SetEqualizerPreset(preset);

        await Assert.That(request.Payload.ToArray())
            .IsEquivalentTo(new byte[] { 0x58, 0x00, presetId, 0x00 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task WhenManualPresetIsSetThenSetEqualizerPresetThrows()
    {
        await Assert.That(() => _commands.SetEqualizerPreset(EqualizerPreset.Manual)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    public async Task WhenEqualizerPresetIsUndefinedThenSetEqualizerPresetThrows()
    {
        await Assert.That(() => _commands.SetEqualizerPreset((EqualizerPreset)0x99)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(5, new[] { -2, 0, 3, 7, 10 }, new byte[] { 0x58, 0x00, 0xA0, 0x06, 15, 8, 10, 13, 17, 20 })]
    [Arguments(-10, new[] { -10, -10, -10, -10, -10 }, new byte[] { 0x58, 0x00, 0xA0, 0x06, 0, 0, 0, 0, 0, 0 })]
    [Arguments(10, new[] { 10, 10, 10, 10, 10 }, new byte[] { 0x58, 0x00, 0xA0, 0x06, 20, 20, 20, 20, 20, 20 })]
    public async Task WhenCustomEqualizerIsSetThenValuesAreOffsetByTen(int clearBass, int[] bands, byte[] payload)
    {
        var request = _commands.SetEqualizerCustom(clearBass, bands);

        await Assert.That(request.Payload.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(new[] { 0, 0, 0, 0 })]
    [Arguments(new[] { 0, 0, 0, 0, 0, 0 })]
    public async Task WhenCustomEqualizerDoesNotHaveFiveBandsThenSetEqualizerCustomThrows(int[] bands)
    {
        await Assert.That(() => _commands.SetEqualizerCustom(0, bands)).ThrowsExactly<ArgumentException>();
    }

    [Test]
    [Arguments(11, new[] { 0, 0, 0, 0, 0 })]
    [Arguments(-11, new[] { 0, 0, 0, 0, 0 })]
    [Arguments(0, new[] { 0, 0, 11, 0, 0 })]
    [Arguments(0, new[] { 0, 0, 0, 0, -11 })]
    public async Task WhenCustomEqualizerValueIsOutOfRangeThenSetEqualizerCustomThrows(int clearBass, int[] bands)
    {
        await Assert.That(() => _commands.SetEqualizerCustom(clearBass, bands)).ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task WhenCustomEqualizerBandsAreNullThenSetEqualizerCustomThrows()
    {
        await Assert.That(() => _commands.SetEqualizerCustom(0, null!)).ThrowsExactly<ArgumentNullException>();
    }

    [Test]
    [Arguments(true, (byte)0x01)]
    [Arguments(false, (byte)0x00)]
    public async Task WhenDseeIsSetThenPayloadCarriesFlag(bool enabled, byte flag)
    {
        var request = _commands.SetDsee(enabled);

        await Assert.That(request.Payload.ToArray()).IsEquivalentTo(new byte[] { 0xE8, 0x01, flag }, CollectionOrdering.Matching);
    }
}
