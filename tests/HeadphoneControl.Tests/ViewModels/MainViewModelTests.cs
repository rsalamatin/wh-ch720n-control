using HeadphoneControl.Diagnostics;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Protocol.Transport;
using HeadphoneControl.Resources;
using HeadphoneControl.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace HeadphoneControl.Tests.ViewModels;

public class MainViewModelTests
{
    private static DeviceState ConnectedState() => new(
        ConnectionStatus.Connected,
        ProtocolGeneration.V2,
        new BatteryState(42, IsCharging: true),
        new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, AmbientLevel: 7),
        new EqualizerState(EqualizerPreset.Off, ClearBass: 0, Bands: [0, 0, 0, 0, 0]),
        DseeEnabled: false,
        FirmwareVersion: "1.0.2",
        AudioCodec.Ldac);

    private static IHeadphoneDevice CreateDevice(DeviceState state)
    {
        var device = Substitute.For<IHeadphoneDevice>();
        device.Name.Returns("WH-CH720N");
        device.State.Returns(state);
        return device;
    }

    private static IHeadphoneDevice CreateNoiseControlEchoingDevice(DeviceState initial)
    {
        var current = initial;
        var device = Substitute.For<IHeadphoneDevice>();
        device.Name.Returns("WH-CH720N");
        device.State.Returns(_ => current);
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                current = current with { NoiseControl = call.Arg<NoiseControlState>() };
                return Task.CompletedTask;
            });
        return device;
    }

    private static MainViewModel CreateViewModel(
        IHeadphoneDevice device,
        Action<Action>? dispatch = null,
        ILogger<MainViewModel>? logger = null,
        ManualDelay? delay = null)
    {
        var inline = dispatch ?? (action => action());
        return new MainViewModel(
            device,
            new DiagnosticsViewModel(new DiagnosticsJournal(), inline),
            logger ?? NullLogger<MainViewModel>.Instance,
            inline,
            TimeSpan.FromMilliseconds(300),
            delay is null ? (_, _) => Task.CompletedTask : delay.DelayAsync);
    }

    [Test]
    public async Task WhenBatteryIsKnownThenBatteryTextShowsLevel()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.BatteryText).IsEqualTo("42 %");
    }

    [Test]
    public async Task WhenBatteryIsUnknownThenBatteryTextIsPlaceholder()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.BatteryText).IsEqualTo(Strings.UnknownValue);
    }

    [Test]
    public async Task WhenConnectedStateAppliedThenCodecTextShowsCodecName()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.CodecText).IsEqualTo("LDAC");
    }

    [Test]
    public async Task WhenCreatedThenPresetListHasTenEntries()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.Presets.Count).IsEqualTo(10);
    }

    [Test]
    public async Task WhenDisconnectedThenSettingsCannotBeEdited()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.CanEditSettings).IsFalse();
    }

    [Test]
    public async Task WhenConnectedAndIdleThenSettingsCanBeEdited()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.CanEditSettings).IsTrue();
    }

    [Test]
    public async Task WhenSettingSendIsRunningThenSettingsStayEditable()
    {
        var device = CreateDevice(ConnectedState());
        var pending = new TaskCompletionSource();
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;

        await Assert.That(viewModel.CanEditEqualizer).IsTrue();
        pending.SetResult();
    }

    [Test]
    public async Task WhenRefreshIsRunningThenViewModelIsBusy()
    {
        var device = CreateDevice(ConnectedState());
        var pending = new TaskCompletionSource();
        device.RefreshAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);

        var refreshing = viewModel.RefreshCommand.ExecuteAsync(null);

        await Assert.That(viewModel.IsBusy).IsTrue();
        pending.SetResult();
        await refreshing;
    }

    [Test]
    public async Task WhenStateArrivesWhileDebouncePendingThenPendingValueIsSent()
    {
        var device = CreateDevice(ConnectedState());
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.AmbientLevel = 15;
        var older = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.Ambient, false, 12) };
        device.State.Returns(older);
        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, older);

        delay.ElapseAll();
        await viewModel.ApplyNoiseControlCommand.ExecutionTask!;

        await device.Received(1).SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, AmbientLevel: 15),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenStateArrivesWhileDebouncePendingThenSliderKeepsUserValue()
    {
        var device = CreateDevice(ConnectedState());
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.AmbientLevel = 15;
        var older = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.Ambient, false, 12) };
        device.State.Returns(older);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, older);

        await Assert.That(viewModel.AmbientLevel).IsEqualTo(15);
        delay.ElapseAll();
    }

    [Test]
    public async Task WhenSliderMovesRapidlyThenOneSendIsMade()
    {
        var device = CreateDevice(ConnectedState());
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.Bands[1].Value = 1;
        viewModel.Bands[1].Value = 2;
        viewModel.Bands[1].Value = 3;

        delay.ElapseAll();
        await viewModel.ApplyCustomEqualizerCommand.ExecutionTask!;

        await device.Received(1).SetCustomEqualizerAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenSliderMovesRapidlyThenLastValueIsSent()
    {
        var device = CreateDevice(ConnectedState());
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.Bands[1].Value = 1;
        viewModel.Bands[1].Value = 2;
        viewModel.Bands[1].Value = 3;

        delay.ElapseAll();
        await viewModel.ApplyCustomEqualizerCommand.ExecutionTask!;

        await device.Received(1).SetCustomEqualizerAsync(
            0,
            Arg.Is<IReadOnlyList<int>>(bands => bands.SequenceEqual(new[] { 0, 3, 0, 0, 0 })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenNewerEditStartsThenInFlightSendIsNotCancelled()
    {
        var device = CreateDevice(ConnectedState());
        var first = new TaskCompletionSource();
        var tokens = new List<CancellationToken>();
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => { tokens.Add(call.Arg<CancellationToken>()); return tokens.Count == 1 ? first.Task : Task.CompletedTask; });
        using var viewModel = CreateViewModel(device);
        viewModel.IsDseeEnabled = true;

        viewModel.IsDseeEnabled = false;

        await Assert.That(tokens[0].IsCancellationRequested).IsFalse();
        first.SetResult();
    }

    [Test]
    public async Task WhenNewerEditSupersedesSendThenStatusIsNotCancelled()
    {
        var device = CreateDevice(ConnectedState());
        var first = new TaskCompletionSource();
        var calls = 0;
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? first.Task : Task.CompletedTask);
        using var viewModel = CreateViewModel(device);
        viewModel.IsDseeEnabled = true;
        var firstRun = viewModel.ApplyDseeCommand.ExecutionTask!;
        viewModel.IsDseeEnabled = false;
        var secondRun = viewModel.ApplyDseeCommand.ExecutionTask!;

        first.SetResult();
        await Task.WhenAll(firstRun, secondRun);

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_Applied"));
    }

    [Test]
    public async Task WhenCancelInvokedDuringSettingSendThenStatusShowsCancelled()
    {
        var device = CreateDevice(ConnectedState());
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        using var viewModel = CreateViewModel(device);
        viewModel.IsDseeEnabled = true;
        var sending = viewModel.ApplyDseeCommand.ExecutionTask!;

        viewModel.CancelCommand.Execute(null);
        await sending;

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_Cancelled"));
    }

    [Test]
    [Arguments(typeof(IOException))]
    [Arguments(typeof(TransportException))]
    [Arguments(typeof(TimeoutException))]
    [Arguments(typeof(FormatException))]
    [Arguments(typeof(InvalidOperationException))]
    [Arguments(typeof(ArgumentException))]
    public async Task WhenDeviceFailsWithContractExceptionThenStatusShowsItsMessage(Type exceptionType)
    {
        var device = CreateDevice(ConnectedState());
        var failure = (Exception)Activator.CreateInstance(exceptionType, "device said no")!;
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(failure);
        using var viewModel = CreateViewModel(device);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        await Assert.That(viewModel.StatusMessage).Contains("device said no");
    }

    [Test]
    public async Task WhenDeviceIsNotConfirmedV2ThenStatusExplainsNothingWasSent()
    {
        var device = CreateDevice(ConnectedState());
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new NotSupportedException("generation V1"));
        using var viewModel = CreateViewModel(device);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Format("Status_NotSupportedFormat", Strings.Get("Operation_Refresh")));
    }

    [Test]
    public async Task WhenDebouncedSendFailsThenStatusShowsError()
    {
        var device = CreateDevice(ConnectedState());
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("no ACK for level"));
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.AmbientLevel = 18;

        delay.ElapseAll();
        await viewModel.ApplyNoiseControlCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusMessage).Contains("no ACK for level");
    }

    [Test]
    public async Task WhenDebouncedSendFailsThenSliderRevertsToDeviceValue()
    {
        var device = CreateDevice(ConnectedState());
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("no ACK for level"));
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);
        viewModel.AmbientLevel = 18;

        delay.ElapseAll();
        await viewModel.ApplyNoiseControlCommand.ExecutionTask!;

        await Assert.That(viewModel.AmbientLevel).IsEqualTo(7);
    }

    [Test]
    public async Task WhenStateChangedDispatchRunsThenLatestSnapshotIsShown()
    {
        var device = CreateDevice(ConnectedState());
        var queued = new List<Action>();
        using var viewModel = CreateViewModel(device, queued.Add);
        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, ConnectedState() with { Battery = new BatteryState(55, false) });
        device.State.Returns(ConnectedState() with { Battery = new BatteryState(54, false) });

        queued.ForEach(action => action());

        await Assert.That(viewModel.BatteryText).IsEqualTo("54 %");
    }

    [Test]
    public async Task WhenLeavingAmbientModeThenFocusOnVoiceIsRemembered()
    {
        var ambientWithVoice = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 7);
        var device = CreateNoiseControlEchoingDevice(ConnectedState() with { NoiseControl = ambientWithVoice });
        using var viewModel = CreateViewModel(device);
        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Off);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Ambient);

        await device.Received(1).SetNoiseControlAsync(ambientWithVoice, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenDeviceEntersNoiseCancellingAfterFocusWasEnabledThenFocusOnVoiceShowsOff()
    {
        var device = CreateNoiseControlEchoingDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        viewModel.FocusOnVoice = true;
        var noiseCancelling = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 1) };
        device.State.Returns(noiseCancelling);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, noiseCancelling);

        await Assert.That(viewModel.FocusOnVoice).IsFalse();
    }

    [Test]
    public async Task WhenSwitchingBackToAmbientThenRememberedFocusOnVoiceIsShown()
    {
        var device = CreateNoiseControlEchoingDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        viewModel.FocusOnVoice = true;
        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.NoiseCancelling);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Ambient);

        await Assert.That(viewModel.FocusOnVoice).IsTrue();
    }

    [Test]
    public async Task WhenSwitchingBackToAmbientThenSentCommandCarriesRememberedFocusOnVoice()
    {
        var device = CreateNoiseControlEchoingDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        viewModel.FocusOnVoice = true;
        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.NoiseCancelling);
        device.ClearReceivedCalls();

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Ambient);

        await device.Received(1).SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 7),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserLeavesAmbientWithFocusEnabledThenOnlyTheModeCommandIsSent()
    {
        var ambientWithVoice = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 7);
        var device = CreateNoiseControlEchoingDevice(ConnectedState() with { NoiseControl = ambientWithVoice });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.NoiseCancelling);

        await device.Received(1).SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenDisconnectedThenConnectCommandCanExecute()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.ConnectCommand.CanExecute(null)).IsTrue();
    }

    [Test]
    public async Task WhenConnectedThenConnectCommandCannotExecute()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.ConnectCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task WhenConnectCommandExecutedThenDeviceConnects()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        using var viewModel = CreateViewModel(device);

        await viewModel.ConnectCommand.ExecuteAsync(null);

        await device.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenConnectFailsThenStatusMessageShowsError()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        device.ConnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TransportException("headset out of range"));
        using var viewModel = CreateViewModel(device);

        await viewModel.ConnectCommand.ExecuteAsync(null);

        await Assert.That(viewModel.StatusMessage).Contains("headset out of range");
    }

    [Test]
    public async Task WhenConnectFailsThenErrorIsLogged()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        device.ConnectAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TransportException("headset out of range"));
        var journal = new DiagnosticsJournal();
        using var provider = new JournalLoggerProvider(journal, logFilePath: null);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        using var viewModel = CreateViewModel(device, logger: loggerFactory.CreateLogger<MainViewModel>());

        await viewModel.ConnectCommand.ExecuteAsync(null);

        await Assert.That(journal.Snapshot().Any(line => line.Contains("[ERR]", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WhenCancelInvokedDuringConnectThenStatusShowsCancelled()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        device.ConnectAsync(Arg.Any<CancellationToken>())
            .Returns(call => Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()));
        using var viewModel = CreateViewModel(device);
        var connecting = viewModel.ConnectCommand.ExecuteAsync(null);

        viewModel.CancelCommand.Execute(null);
        await connecting;

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_Cancelled"));
    }

    [Test]
    public async Task WhenStateChangedThenUpdateWaitsForDispatcher()
    {
        var device = CreateDevice(ConnectedState());
        var queued = new List<Action>();
        using var viewModel = CreateViewModel(device, queued.Add);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, ConnectedState() with { Battery = new BatteryState(55, false) });

        await Assert.That(viewModel.BatteryText).IsEqualTo("42 %");
    }

    [Test]
    public async Task WhenDispatchedStateChangeRunsThenViewModelShowsNewState()
    {
        var device = CreateDevice(ConnectedState());
        var queued = new List<Action>();
        using var viewModel = CreateViewModel(device, queued.Add);
        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, ConnectedState() with { Battery = new BatteryState(55, false) });

        device.State.Returns(ConnectedState() with { Battery = new BatteryState(55, false) });

        queued.ForEach(action => action());

        await Assert.That(viewModel.BatteryText).IsEqualTo("55 %");
    }

    [Test]
    public async Task WhenDeviceReportsNewPresetThenNoPresetCommandIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var changed = ConnectedState() with { Equalizer = new EqualizerState(EqualizerPreset.BassBoost, 0, [0, 0, 0, 0, 0]) };
        device.State.Returns(changed);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, changed);

        await device.DidNotReceive().SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenDeviceReportsNewPresetThenPresetIsSelected()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var changed = ConnectedState() with { Equalizer = new EqualizerState(EqualizerPreset.BassBoost, 0, [0, 0, 0, 0, 0]) };
        device.State.Returns(changed);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, changed);

        await Assert.That(viewModel.SelectedPreset?.Value).IsEqualTo(EqualizerPreset.BassBoost);
    }

    [Test]
    public async Task WhenUserSelectsPresetThenDeviceReceivesPreset()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.BassBoost);

        await device.Received(1).SetEqualizerPresetAsync(EqualizerPreset.BassBoost, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenPresetIsAppliedButEqualizerCannotBeReadBackThenStatusAsksForRefresh()
    {
        var device = CreateDevice(ConnectedState());
        device.SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                device.State.Returns(ConnectedState() with { Equalizer = null });
                return Task.CompletedTask;
            });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.BassBoost);
        await viewModel.ApplyEqualizerPresetCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_EqualizerUnknown"));
    }

    [Test]
    public async Task WhenPresetIsAppliedAndEqualizerIsKnownThenStatusIsApplied()
    {
        var device = CreateDevice(ConnectedState());
        device.SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                device.State.Returns(ConnectedState() with { Equalizer = new EqualizerState(EqualizerPreset.BassBoost, 0, [0, 0, 0, 0, 0]) });
                return Task.CompletedTask;
            });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.BassBoost);
        await viewModel.ApplyEqualizerPresetCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_Applied"));
    }

    [Test]
    public async Task WhenUserMovesBandThenCustomEqualizerIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.Bands[2].Value = 4;

        await device.Received(1).SetCustomEqualizerAsync(
            0,
            Arg.Is<IReadOnlyList<int>>(bands => bands.SequenceEqual(new[] { 0, 0, 4, 0, 0 })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserMovesBandThenPresetShowsManual()
    {
        var device = CreateDevice(ConnectedState());
        var delay = new ManualDelay();
        using var viewModel = CreateViewModel(device, delay: delay);

        viewModel.Bands[0].Value = -3;

        await Assert.That(viewModel.SelectedPreset?.Value).IsEqualTo(EqualizerPreset.Manual);
        delay.ElapseAll();
    }

    [Test]
    public async Task WhenUserMovesBandThenNoPresetCommandIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.ClearBass.Value = 6;

        await device.DidNotReceive().SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserSelectsAmbientThenLastAmbientLevelIsSent()
    {
        var device = CreateNoiseControlEchoingDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.NoiseCancelling);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Ambient);

        await device.Received(1).SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, AmbientLevel: 7),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserMovesAmbientSliderThenNewLevelIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.AmbientLevel = 15;

        await device.Received(1).SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, AmbientLevel: 15),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenNotInAmbientModeThenAmbientControlsAreDisabled()
    {
        var noiseCancelling = new NoiseControlState(NoiseControlMode.NoiseCancelling, FocusOnVoice: false, AmbientLevel: 1);
        var device = CreateDevice(ConnectedState() with { NoiseControl = noiseCancelling });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.CanEditAmbient).IsFalse();
    }

    [Test]
    public async Task WhenDeviceIsInAmbientModeThenAmbientControlsAreEnabled()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.CanEditAmbient).IsTrue();
    }

    [Test]
    public async Task WhenDeviceEntersNoiseCancellingThenAmbientLevelShowsRememberedLevel()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var noiseCancelling = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.NoiseCancelling, false, 1) };
        device.State.Returns(noiseCancelling);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, noiseCancelling);

        await Assert.That(viewModel.AmbientLevel).IsEqualTo(7);
    }

    [Test]
    public async Task WhenUserTogglesDseeThenDeviceReceivesDsee()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;

        await device.Received(1).SetDseeAsync(true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenSetterFailsThenValueRevertsToDeviceState()
    {
        var device = CreateDevice(ConnectedState());
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;
        await viewModel.ApplyDseeCommand.ExecutionTask!;

        await Assert.That(viewModel.IsDseeEnabled).IsFalse();
    }

    [Test]
    public async Task WhenSetterFailsThenStatusMessageShowsError()
    {
        var device = CreateDevice(ConnectedState());
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;
        await viewModel.ApplyDseeCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusMessage).Contains("no ACK");
    }
}
