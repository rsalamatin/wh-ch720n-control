using HeadphoneControl.Diagnostics;
using HeadphoneControl.FirmwareUpdates;
using HeadphoneControl.Settings;
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
    private static readonly Task<EditOutcome> Applied = Task.FromResult(EditOutcome.Applied);

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
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                current = current with { NoiseControl = call.Arg<NoiseControlState>() };
                return Applied;
            });
        return device;
    }

    private static MainViewModel CreateViewModel(
        IHeadphoneDevice device,
        Action<Action>? dispatch = null,
        ILogger<MainViewModel>? logger = null)
    {
        var inline = dispatch ?? (action => action());
        return new MainViewModel(
            device,
            new DiagnosticsViewModel(new DiagnosticsJournal(), inline),
            new FirmwareUpdateViewModel(
                device,
                _ => Task.FromResult<FirmwareRelease?>(null),
                new FirmwareCheckSettings(Automatic: false),
                _ => { },
                NullLogger<FirmwareUpdateViewModel>.Instance,
                inline),
            logger ?? NullLogger<MainViewModel>.Instance,
            inline);
    }

    [Test]
    public async Task WhenBatteryIsKnownThenBatteryTextShowsLevel()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.BatteryText).IsEqualTo("42 %");
    }

    [Test]
    public async Task WhenBatteryIsKnownThenTrayToolTipShowsConnectionAndBattery()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.TrayToolTip).IsEqualTo(Strings.Format(
            "TrayToolTipWithBatteryFormat", Strings.AppTitle, Strings.Get("ConnectionStatus_Connected"), "42 %"));
    }

    [Test]
    public async Task WhenDisconnectedThenTrayToolTipOmitsBattery()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.TrayToolTip).IsEqualTo(Strings.Format(
            "TrayToolTipFormat", Strings.AppTitle, Strings.Get("ConnectionStatus_Disconnected")));
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
        var pending = new TaskCompletionSource<EditOutcome>();
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;

        await Assert.That(viewModel.CanEditEqualizer).IsTrue();
        pending.SetResult(EditOutcome.Applied);
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
    public async Task WhenStateArrivesWhileNoiseEditIsPendingThenSliderKeepsUserValue()
    {
        var device = CreateDevice(ConnectedState());
        device.HasPendingEdit(SettingGroup.NoiseControl).Returns(true);
        using var viewModel = CreateViewModel(device);
        viewModel.AmbientLevel = 15;
        var older = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.Ambient, false, 12) };
        device.State.Returns(older);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, older);

        await Assert.That(viewModel.AmbientLevel).IsEqualTo(15);
    }

    [Test]
    public async Task WhenStateArrivesWhileOnlyAnotherGroupIsPendingThenSliderShowsDeviceValue()
    {
        var device = CreateDevice(ConnectedState());
        var inFlight = new TaskCompletionSource<EditOutcome>();
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>())
            .Returns(inFlight.Task);
        device.HasPendingEdit(SettingGroup.Equalizer).Returns(true);
        using var viewModel = CreateViewModel(device);
        viewModel.AmbientLevel = 15;
        var older = ConnectedState() with { NoiseControl = new NoiseControlState(NoiseControlMode.Ambient, false, 12) };
        device.State.Returns(older);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, older);

        await Assert.That(viewModel.AmbientLevel).IsEqualTo(12);
    }

    [Test]
    public async Task WhenEditIsSupersededThenAnEarlierErrorStaysVisible()
    {
        var device = CreateDevice(ConnectedState());
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(EditOutcome.Superseded));
        using var viewModel = CreateViewModel(device);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.IsDseeEnabled = true;
        await viewModel.ApplyDseeCommand.ExecutionTask!;

        await Assert.That(viewModel.IsAlertVisible).IsTrue();
    }

    [Test]
    public async Task WhenUserMovesAmbientSliderThenTheEditIsDebounced()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.AmbientLevel = 15;

        await device.Received(1).SetNoiseControlAsync(
            Arg.Any<NoiseControlState>(), EditPacing.Debounced, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserMovesBandThenTheEditIsDebounced()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.Bands[1].Value = 3;

        await device.Received(1).SetCustomEqualizerAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<int>>(), EditPacing.Debounced, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserSelectsNoiseModeThenTheEditIsImmediate()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Off);

        await device.Received(1).SetNoiseControlAsync(
            Arg.Any<NoiseControlState>(), EditPacing.Immediate, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserSelectsManualPresetThenTheShownCurveIsSentAsCustomEqualizer()
    {
        var device = CreateDevice(ConnectedState() with
        {
            Equalizer = new EqualizerState(EqualizerPreset.BassBoost, 1, [6, 4, 0, 0, 0]),
        });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.Manual);

        await device.Received(1).SetCustomEqualizerAsync(
            1,
            Arg.Is<IReadOnlyList<int>>(bands => bands.SequenceEqual(new[] { 6, 4, 0, 0, 0 })),
            EditPacing.Immediate,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserSelectsManualPresetThenNoPresetCommandIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.Manual);

        await device.DidNotReceive().SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenNewerEditStartsThenInFlightSendIsNotCancelled()
    {
        var device = CreateDevice(ConnectedState());
        var first = new TaskCompletionSource<EditOutcome>();
        var tokens = new List<CancellationToken>();
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => { tokens.Add(call.Arg<CancellationToken>()); return tokens.Count == 1 ? first.Task : Applied; });
        using var viewModel = CreateViewModel(device);
        viewModel.IsDseeEnabled = true;

        viewModel.IsDseeEnabled = false;

        await Assert.That(tokens[0].IsCancellationRequested).IsFalse();
        first.SetResult(EditOutcome.Applied);
    }

    [Test]
    public async Task WhenNewerEditSupersedesSendThenStatusIsNotCancelled()
    {
        var device = CreateDevice(ConnectedState());
        var first = new TaskCompletionSource<EditOutcome>();
        var calls = 0;
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? first.Task : Applied);
        using var viewModel = CreateViewModel(device);
        viewModel.IsDseeEnabled = true;
        var firstRun = viewModel.ApplyDseeCommand.ExecutionTask!;
        viewModel.IsDseeEnabled = false;
        var secondRun = viewModel.ApplyDseeCommand.ExecutionTask!;

        first.SetResult(EditOutcome.Applied);
        await Task.WhenAll(firstRun, secondRun);

        await Assert.That(viewModel.StatusMessage).IsEqualTo(Strings.Get("Status_Applied"));
    }

    [Test]
    public async Task WhenCancelInvokedDuringSettingSendThenStatusShowsCancelled()
    {
        var device = CreateDevice(ConnectedState());
        device.SetDseeAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(async call => { await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>()); return EditOutcome.Applied; });
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
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("no ACK for level"));
        using var viewModel = CreateViewModel(device);

        viewModel.AmbientLevel = 18;
        await viewModel.ApplyNoiseControlCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusMessage).Contains("no ACK for level");
    }

    [Test]
    public async Task WhenDebouncedSendFailsThenSliderRevertsToDeviceValue()
    {
        var device = CreateDevice(ConnectedState());
        device.SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("no ACK for level"));
        using var viewModel = CreateViewModel(device);

        viewModel.AmbientLevel = 18;
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

    private static void ReportBattery(IHeadphoneDevice device, int level, bool isCharging = false)
    {
        var state = ConnectedState() with { Battery = new BatteryState(level, isCharging) };
        device.State.Returns(state);
        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, state);
    }

    private static void ReportDisconnected(IHeadphoneDevice device)
    {
        device.State.Returns(DeviceState.Disconnected);
        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, DeviceState.Disconnected);
    }

    [Test]
    public async Task WhenBatteryFallsToTheLowLevelThenLowBatteryIsReportedWithTheLevel()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);

        ReportBattery(device, 20);

        await Assert.That(reported).IsEquivalentTo([20]);
    }

    [Test]
    public async Task WhenBatteryIsAboveTheLowLevelThenLowBatteryIsNotReported()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);

        ReportBattery(device, 21);

        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task WhenBatteryKeepsFallingThenLowBatteryIsReportedOnce()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);
        ReportBattery(device, 20);

        ReportBattery(device, 19);

        await Assert.That(reported).IsEquivalentTo([20]);
    }

    [Test]
    public async Task WhenBatteryWaversJustAboveTheLowLevelThenLowBatteryIsReportedOnce()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);
        ReportBattery(device, 20);
        ReportBattery(device, 22);

        ReportBattery(device, 20);

        await Assert.That(reported).IsEquivalentTo([20]);
    }

    [Test]
    public async Task WhenLowBatteryIsChargingThenLowBatteryIsNotReported()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);

        ReportBattery(device, 10, isCharging: true);

        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task WhenChargingStopsWhileStillLowThenLowBatteryIsReportedAgain()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);
        ReportBattery(device, 15);
        ReportBattery(device, 16, isCharging: true);

        ReportBattery(device, 17);

        await Assert.That(reported).IsEquivalentTo([15, 17]);
    }

    [Test]
    public async Task WhenBatteryRecoversAndFallsAgainThenLowBatteryIsReportedAgain()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);
        ReportBattery(device, 20);
        ReportBattery(device, 25);

        ReportBattery(device, 19);

        await Assert.That(reported).IsEquivalentTo([20, 19]);
    }

    [Test]
    public async Task WhenHeadsetReconnectsWithLowBatteryThenLowBatteryIsReportedAgain()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var reported = new List<int>();
        viewModel.LowBatteryReached += (_, level) => reported.Add(level);
        ReportBattery(device, 18);
        ReportDisconnected(device);

        ReportBattery(device, 18);

        await Assert.That(reported).IsEquivalentTo([18, 18]);
    }

    [Test]
    public async Task WhenLeavingAmbientModeThenFocusOnVoiceIsRemembered()
    {
        var ambientWithVoice = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 7);
        var device = CreateNoiseControlEchoingDevice(ConnectedState() with { NoiseControl = ambientWithVoice });
        using var viewModel = CreateViewModel(device);
        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Off);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.Ambient);

        await device.Received(1).SetNoiseControlAsync(ambientWithVoice, Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
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
            Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserLeavesAmbientWithFocusEnabledThenOnlyTheModeCommandIsSent()
    {
        var ambientWithVoice = new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: true, AmbientLevel: 7);
        var device = CreateNoiseControlEchoingDevice(ConnectedState() with { NoiseControl = ambientWithVoice });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedNoiseMode = viewModel.NoiseModes.Single(m => m.Value == NoiseControlMode.NoiseCancelling);

        await device.Received(1).SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
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
                return Applied;
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
                return Applied;
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
            Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserMovesBandThenPresetShowsManual()
    {
        var device = CreateDevice(ConnectedState());
        device.HasPendingEdit(SettingGroup.Equalizer).Returns(true);
        using var viewModel = CreateViewModel(device);

        viewModel.Bands[0].Value = -3;

        await Assert.That(viewModel.SelectedPreset?.Value).IsEqualTo(EqualizerPreset.Manual);
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
            Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenUserMovesAmbientSliderThenNewLevelIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.AmbientLevel = 15;

        await device.Received(1).SetNoiseControlAsync(
            new NoiseControlState(NoiseControlMode.Ambient, FocusOnVoice: false, AmbientLevel: 15),
            Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
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
    public async Task WhenDeviceFailsThenAlertIsVisible()
    {
        var device = CreateDevice(ConnectedState());
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        using var viewModel = CreateViewModel(device);

        await viewModel.RefreshCommand.ExecuteAsync(null);

        await Assert.That(viewModel.IsAlertVisible).IsTrue();
    }

    [Test]
    public async Task WhenOperationSucceedsAfterFailureThenAlertIsHidden()
    {
        var device = CreateDevice(ConnectedState());
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        using var viewModel = CreateViewModel(device);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.IsDseeEnabled = true;
        await viewModel.ApplyDseeCommand.ExecutionTask!;

        await Assert.That(viewModel.IsAlertVisible).IsFalse();
    }

    [Test]
    public async Task WhenAlertIsDismissedThenAlertIsHidden()
    {
        var device = CreateDevice(ConnectedState());
        device.RefreshAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("no ACK"));
        using var viewModel = CreateViewModel(device);
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.DismissAlertCommand.Execute(null);

        await Assert.That(viewModel.IsAlertVisible).IsFalse();
    }

    [Test]
    public async Task WhenSettingIsAppliedThenNoAlertIsShown()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.IsDseeEnabled = true;
        await viewModel.ApplyDseeCommand.ExecutionTask!;

        await Assert.That(viewModel.IsAlertVisible).IsFalse();
    }

    [Test]
    public async Task WhenPresetIsAppliedButEqualizerCannotBeReadBackThenStatusIsWarning()
    {
        var device = CreateDevice(ConnectedState());
        device.SetEqualizerPresetAsync(Arg.Any<EqualizerPreset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                device.State.Returns(ConnectedState() with { Equalizer = null });
                return Applied;
            });
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedPreset = viewModel.Presets.Single(p => p.Value == EqualizerPreset.BassBoost);
        await viewModel.ApplyEqualizerPresetCommand.ExecutionTask!;

        await Assert.That(viewModel.StatusSeverity).IsEqualTo(StatusSeverity.Warning);
    }

    [Test]
    [Arguments(20, true)]
    [Arguments(21, false)]
    public async Task WhenBatteryLevelIsKnownThenLowBatteryStartsAtTwentyPercent(int level, bool expectedLow)
    {
        var device = CreateDevice(ConnectedState() with { Battery = new BatteryState(level, IsCharging: false) });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.IsBatteryLow).IsEqualTo(expectedLow);
    }

    [Test]
    public async Task WhenConnectedWithKnownCodecThenConnectionSummaryShowsCodec()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.ConnectionSummary).IsEqualTo(Strings.Format("ConnectionWithCodecFormat", Strings.Get("ConnectionStatus_Connected"), "LDAC"));
    }

    [Test]
    public async Task WhenConnectIsRunningThenViewModelIsConnecting()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        var pending = new TaskCompletionSource();
        device.ConnectAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);

        var connecting = viewModel.ConnectCommand.ExecuteAsync(null);

        try
        {
            await Assert.That(viewModel.IsConnecting).IsTrue();
        }
        finally
        {
            pending.SetResult();
            await connecting;
        }
    }

    [Test]
    public async Task WhenDisconnectIsRunningAndDeviceReportsDisconnectedThenViewModelIsNotConnecting()
    {
        var device = CreateDevice(ConnectedState());
        var pending = new TaskCompletionSource();
        device.DisconnectAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);
        var disconnecting = viewModel.DisconnectCommand.ExecuteAsync(null);
        device.State.Returns(DeviceState.Disconnected);

        device.StateChanged += Raise.Event<EventHandler<DeviceState>>(device, DeviceState.Disconnected);

        try
        {
            await Assert.That(viewModel.IsConnecting).IsFalse();
        }
        finally
        {
            pending.SetResult();
            await disconnecting;
        }
    }

    [Test]
    public async Task WhenConnectIsRunningThenEmptyStateTitleShowsConnecting()
    {
        var device = CreateDevice(DeviceState.Disconnected);
        var pending = new TaskCompletionSource();
        device.ConnectAsync(Arg.Any<CancellationToken>()).Returns(pending.Task);
        using var viewModel = CreateViewModel(device);

        var connecting = viewModel.ConnectCommand.ExecuteAsync(null);

        try
        {
            await Assert.That(viewModel.EmptyStateTitle).IsEqualTo(Strings.Get("ConnectionStatus_Connecting"));
        }
        finally
        {
            pending.SetResult();
            await connecting;
        }
    }

    [Test]
    public async Task WhenAmbientLevelChangesThenNoiseSummaryChangeIsRaised()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.AmbientLevel = 12;

        await Assert.That(raised).Contains(nameof(MainViewModel.NoiseSummary));
    }

    [Test]
    public async Task WhenEqualizerIsUnknownThenSoundSummaryShowsPlaceholderPreset()
    {
        var device = CreateDevice(ConnectedState() with { Equalizer = null });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.SoundSummary).IsEqualTo(Strings.Format("Summary_SoundFormat", Strings.UnknownValue, Strings.Get("Off")));
    }

    [Test]
    public async Task WhenNoiseModeSelectionIsClearedThenDeviceModeIsShownAgain()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedNoiseMode = null;

        await Assert.That(viewModel.SelectedNoiseMode?.Value).IsEqualTo(NoiseControlMode.Ambient);
    }

    [Test]
    public async Task WhenNoiseModeSelectionIsClearedThenNothingIsSent()
    {
        var device = CreateDevice(ConnectedState());
        using var viewModel = CreateViewModel(device);

        viewModel.SelectedNoiseMode = null;

        await device.DidNotReceive().SetNoiseControlAsync(Arg.Any<NoiseControlState>(), Arg.Any<EditPacing>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task WhenEqualizerIsUnknownThenResetCannotExecute()
    {
        var device = CreateDevice(ConnectedState() with { Equalizer = null });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.ResetEqualizerCommand.CanExecute(null)).IsFalse();
    }

    [Test]
    public async Task WhenConnectionFailedThenConnectButtonOffersRetry()
    {
        var device = CreateDevice(DeviceState.Disconnected with { Connection = ConnectionStatus.Failed });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.ConnectButtonText).IsEqualTo(Strings.Get("TryAgain"));
    }

    [Test]
    public async Task WhenCreatedThenNoiseCancellingIsTheFirstNoiseMode()
    {
        var device = CreateDevice(DeviceState.Disconnected);

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.NoiseModes[0].Value).IsEqualTo(NoiseControlMode.NoiseCancelling);
    }

    [Test]
    public async Task WhenInAmbientModeThenNoiseSummaryIncludesAmbientLevel()
    {
        var device = CreateDevice(ConnectedState());

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.NoiseSummary).IsEqualTo(Strings.Format("Summary_AmbientFormat", Strings.Get("NoiseMode_Ambient"), 7));
    }

    [Test]
    public async Task WhenNotInAmbientModeThenNoiseSummaryIsModeName()
    {
        var noiseCancelling = new NoiseControlState(NoiseControlMode.NoiseCancelling, FocusOnVoice: false, AmbientLevel: 1);
        var device = CreateDevice(ConnectedState() with { NoiseControl = noiseCancelling });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.NoiseSummary).IsEqualTo(Strings.Get("NoiseMode_NoiseCancelling"));
    }

    [Test]
    public async Task WhenDseeIsOnThenSoundSummarySaysOn()
    {
        var device = CreateDevice(ConnectedState() with { DseeEnabled = true });

        using var viewModel = CreateViewModel(device);

        await Assert.That(viewModel.SoundSummary).IsEqualTo(Strings.Format("Summary_SoundFormat", Strings.Get("EqPreset_Off"), Strings.Get("On")));
    }

    [Test]
    public async Task WhenEqualizerIsResetThenOffPresetIsSent()
    {
        var device = CreateDevice(ConnectedState() with { Equalizer = new EqualizerState(EqualizerPreset.BassBoost, 0, [0, 0, 0, 0, 0]) });
        using var viewModel = CreateViewModel(device);

        viewModel.ResetEqualizerCommand.Execute(null);

        await device.Received(1).SetEqualizerPresetAsync(EqualizerPreset.Off, Arg.Any<CancellationToken>());
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
