using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HeadphoneControl.Core;
using HeadphoneControl.Diagnostics;
using HeadphoneControl.Platform.Windows;
using HeadphoneControl.Protocol.Devices;
using HeadphoneControl.Simulation;
using HeadphoneControl.ViewModels;
using HeadphoneControl.Views;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl;

public partial class App : Application
{
    private const string SimulatedSwitch = "--simulated";
    private const string SimulatedConnectFailureSwitch = "--simulated-connect-failure";
    private const string VerboseSwitch = "--verbose";
    private const string HeadsetModel = "WH-CH720N";
    private static readonly TimeSpan SimulatedLatency = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DeviceShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CrashFlushTimeout = TimeSpan.FromSeconds(2);

    private JournalLoggerProvider? _logProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private IHeadphoneDevice? _device;
    private MainViewModel? _mainViewModel;
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

            Action<Action> dispatch = action => Dispatcher.UIThread.Post(action);
            _mainViewModel = new MainViewModel(
                _device,
                new DiagnosticsViewModel(journal, dispatch),
                _loggerFactory.CreateLogger<MainViewModel>(),
                dispatch);

            var window = new MainWindow { DataContext = _mainViewModel };
            window.Closing += OnMainWindowClosing;
            desktop.MainWindow = window;
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The only place that picks the headset implementation and the platform backend.
    private static IHeadphoneDevice CreateDevice(string[] args, ILoggerFactory loggerFactory)
    {
        var failConnect = args.Contains(SimulatedConnectFailureSwitch, StringComparer.OrdinalIgnoreCase);
        if (failConnect || args.Contains(SimulatedSwitch, StringComparer.OrdinalIgnoreCase))
        {
            var simulated = new SimulatedHeadsetConnector(SimulatedLatency) { FailNextConnect = failConnect };
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
        _loggerFactory?.Dispose();
        _logProvider?.Dispose();
    }
}
