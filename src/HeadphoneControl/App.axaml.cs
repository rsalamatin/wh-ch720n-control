using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using HeadphoneControl.Core;
using HeadphoneControl.Diagnostics;
using HeadphoneControl.FirmwareUpdates;
using HeadphoneControl.Platform.Windows;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Resources;
using HeadphoneControl.Settings;
using HeadphoneControl.Simulation;
using HeadphoneControl.ViewModels;
using HeadphoneControl.Views;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl;

public partial class App : Application
{
    private const string SimulatedSwitch = "--simulated";
    private const string SimulatedConnectFailureSwitch = "--simulated-connect-failure";
    private const string SimulatedBatterySwitch = "--simulated-battery=";
    private const string FirmwareManifestSwitch = "--firmware-manifest=";
    private const string VerboseSwitch = "--verbose";
    private const string HeadsetModel = "WH-CH720N";
    private const string ToastAppId = "HeadphoneControl";
    private const string IconFileName = "headphone-control.ico";
    private static readonly Uri AppIconUri = new($"avares://HeadphoneControl/Assets/{IconFileName}");
    private static readonly Uri DefaultFirmwareManifestUrl =
        new("https://raw.githubusercontent.com/rsalamatin/wh-ch720n-control/main/firmware.json");
    private static readonly TimeSpan FirmwareCheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SimulatedLatency = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DeviceShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CrashFlushTimeout = TimeSpan.FromSeconds(2);

    private JournalLoggerProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private IHeadphoneDevice? _device;
    private MainViewModel? _mainViewModel;
    private UiSettingsStore? _settingsStore;
    private UiSettings? _settings;
    private TrayIcon? _trayIcon;
    private Bitmap? _appIcon;
    private HttpClient? _http;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var journal = new DiagnosticsJournal();
            _logProvider = new JournalLoggerProvider(journal, JournalLoggerProvider.DefaultLogFilePath);
            // Debug includes every raw protocol frame, and the headset streams now-playing titles over the same
            // channel, so frame dumps are opt-in.
            var verbose = args.Contains(VerboseSwitch, StringComparer.OrdinalIgnoreCase);
            _loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information)
                .AddProvider(_logProvider));
            _logger = _loggerFactory.CreateLogger<App>();
            HookUnhandledExceptions(_logger, _logProvider);
            _logger.LogInformation("Starting with arguments: {Arguments}", string.Join(' ', args));

            _device = CreateDevice(args, _loggerFactory);

            _settingsStore = new UiSettingsStore(UiSettingsStore.DefaultPath, _loggerFactory.CreateLogger<UiSettingsStore>());
            _settings = _settingsStore.Load();
            ApplyTheme(_settings.Theme);

            Action<Action> dispatch = action => Dispatcher.UIThread.Post(action);
            _mainViewModel = new MainViewModel(
                _device,
                new DiagnosticsViewModel(journal, dispatch),
                CreateFirmwareUpdates(args, _device, _settings, dispatch, _loggerFactory),
                _loggerFactory.CreateLogger<MainViewModel>(),
                dispatch);

            var window = new MainWindow { DataContext = _mainViewModel };
            window.ShowTheme(_settings.Theme);
            window.ThemeSelected += OnThemeSelected;
            window.Closing += OnMainWindowClosing;
            CreateTrayIcon(window, _mainViewModel);
            ShowNotifications(_mainViewModel, _loggerFactory);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = window;
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(MainWindow window, MainViewModel viewModel)
    {
        var open = new NativeMenuItem(Strings.Tray_Open);
        open.Click += (_, _) => window.RestoreFromTray();
        var exit = new NativeMenuItem(Strings.Tray_Exit);
        exit.Click += (_, _) => window.Close();

        _trayIcon = new TrayIcon
        {
            Icon = window.Icon,
            Menu = [open, new NativeMenuItemSeparator(), exit],
        };
        _trayIcon.Bind(TrayIcon.ToolTipTextProperty, new Binding(nameof(MainViewModel.TrayToolTip)) { Source = viewModel });
        _trayIcon.Clicked += (_, _) => window.RestoreFromTray();
        TrayIcon.SetIcons(this, [_trayIcon]);
        window.MinimizeToTray = true;

        using (var icon = AssetLoader.Open(AppIconUri))
        {
            _appIcon = new Bitmap(icon);
        }

        var plainIcon = window.Icon;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.HasBattery) or nameof(MainViewModel.BatteryLevel))
            {
                _trayIcon.Icon = viewModel.HasBattery
                    ? BatteryTrayIcon.Render(_appIcon, viewModel.BatteryLevel, viewModel.IsBatteryLow)
                    : plainIcon;
            }
        };
    }

    // The check always asks about the real model, so --simulated shows it too.
    private FirmwareUpdateViewModel CreateFirmwareUpdates(
        string[] args, IHeadphoneDevice device, UiSettings settings, Action<Action> dispatch, ILoggerFactory loggerFactory)
    {
        var manifestUrl = args.FirstOrDefault(a => a.StartsWith(FirmwareManifestSwitch, StringComparison.OrdinalIgnoreCase))
            is { } argument
            ? new Uri(argument[FirmwareManifestSwitch.Length..])
            : DefaultFirmwareManifestUrl;
        _http = new HttpClient();
        var checker = new FirmwareUpdateChecker(_http, manifestUrl, FirmwareCheckTimeout);
        return new FirmwareUpdateViewModel(
            device,
            cancellationToken => checker.GetLatestAsync(HeadsetModel, cancellationToken),
            settings.FirmwareCheck ?? new FirmwareCheckSettings(),
            SaveFirmwareCheck,
            loggerFactory.CreateLogger<FirmwareUpdateViewModel>(),
            dispatch);
    }

    private void SaveFirmwareCheck(FirmwareCheckSettings firmwareCheck)
    {
        _settings = (_settings ?? UiSettings.Default) with { FirmwareCheck = firmwareCheck };
        _settingsStore?.Save(_settings);
    }

    private static void ShowNotifications(MainViewModel viewModel, ILoggerFactory loggerFactory)
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, IconFileName);
        var notifier = new ToastNotifier(
            ToastAppId,
            Strings.AppTitle,
            File.Exists(iconPath) ? iconPath : null,
            loggerFactory.CreateLogger<ToastNotifier>());
        viewModel.LowBatteryReached += (_, level) => notifier.Show(
            Strings.Get("LowBattery_Title"), Strings.Format("LowBattery_BodyFormat", viewModel.DeviceName, level));
        viewModel.Firmware.UpdateFound += (_, update) => notifier.Show(
            Strings.Get("FirmwareUpdate_Title"),
            Strings.Format("FirmwareUpdate_BodyFormat", viewModel.DeviceName, update.InstalledVersion, update.LatestVersion));
    }

    private void OnThemeSelected(object? sender, ThemePreference theme)
    {
        ApplyTheme(theme);
        _settings = (_settings ?? UiSettings.Default) with { Theme = theme };
        _settingsStore?.Save(_settings);
    }

    private void ApplyTheme(ThemePreference theme) => RequestedThemeVariant = theme switch
    {
        ThemePreference.Light => ThemeVariant.Light,
        ThemePreference.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    // The only place that picks the headset implementation and its platform backend.
    private static IHeadphoneDevice CreateDevice(string[] args, ILoggerFactory loggerFactory)
    {
        var failConnect = args.Contains(SimulatedConnectFailureSwitch, StringComparer.OrdinalIgnoreCase);
        var batteryArgument = args.FirstOrDefault(
            a => a.StartsWith(SimulatedBatterySwitch, StringComparison.OrdinalIgnoreCase))?[SimulatedBatterySwitch.Length..];
        if (failConnect || batteryArgument is not null || args.Contains(SimulatedSwitch, StringComparer.OrdinalIgnoreCase))
        {
            var batteryLevel = SimulatedHeadsetConnector.DefaultBatteryLevel;
            if (batteryArgument is not null && (!int.TryParse(batteryArgument, out batteryLevel) || batteryLevel is < 0 or > 100))
            {
                throw new ArgumentException($"{SimulatedBatterySwitch} needs a level from 0 to 100, not '{batteryArgument}'.");
            }

            var simulated = new SimulatedHeadsetConnector(SimulatedLatency, batteryLevel: batteryLevel) { FailNextConnect = failConnect };
            return new HeadsetController(SimulatedHeadsetConnector.HeadsetName, simulated.ConnectPreferredAsync, loggerFactory);
        }

        IHeadsetConnector connector = new RfcommConnector(loggerFactory);
        return new HeadsetController(HeadsetModel, connector.ConnectPreferredAsync, loggerFactory);
    }

    private static void HookUnhandledExceptions(ILogger logger, JournalLoggerProvider logProvider)
    {
        // The process may die right after these handlers, so the log is flushed synchronously (bounded).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {IsTerminating})", e.IsTerminating);
            logProvider.Flush(CrashFlushTimeout);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            logger.LogError(e.Exception, "Unobserved task exception");
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogCritical(e.Exception, "Unhandled exception on the UI thread");
            logProvider.Flush(CrashFlushTimeout);
        };
    }

    // The device holds the Bluetooth link, so the window only closes once it has been released.
    private async void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_shutdownCompleted || sender is not Window window)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        try
        {
            _mainViewModel?.Dispose();
            if (_device is not null)
            {
                await _device.DisposeAsync().AsTask().WaitAsync(DeviceShutdownTimeout);
            }
        }
        catch (Exception ex)
        {
            // async void boundary during exit: nothing is left to recover, so log and close instead of crashing.
            _logger?.LogError(ex, "Releasing the headset failed during shutdown");
        }
        finally
        {
            _shutdownCompleted = true;
            window.Close();
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _logger?.LogInformation("Exiting with code {ExitCode}", e.ApplicationExitCode);
        _trayIcon?.Dispose();
        _appIcon?.Dispose();
        _http?.Dispose();
        _loggerFactory?.Dispose();
        _logProvider?.Dispose();
    }
}
