using HeadphoneControl.Core;
using HeadphoneControl.Diagnostics;
using HeadphoneControl.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Tests.Hardware;

// Needs the paired WH-CH720N. Read-only: sends only the init handshake and GET queries, never a SET.
[NotInParallel("rfcomm-hardware")]
public class HeadphoneDeviceHardwareTests
{
    [Test]
    [Explicit]
    public async Task WhenRealHeadsetConnectsThenEverySettingIsRead()
    {
        var journal = new DiagnosticsJournal(5000);
        using var loggers = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddProvider(new JournalLoggerProvider(journal, logFilePath: null)));
        IHeadsetConnector connector = new RfcommConnector(loggers);
        await using var device = new HeadsetController("WH-CH720N", connector.ConnectPreferredAsync, loggers);

        await device.ConnectAsync(CancellationToken.None);

        var state = device.State;
        Console.WriteLine(state);
        Console.WriteLine(string.Join(Environment.NewLine, journal.Snapshot()));
        await Assert.That(new object?[]
        {
            state.Battery, state.NoiseControl, state.Equalizer, state.DseeEnabled,
            state.FirmwareVersion, state.Codec,
        }).DoesNotContain(null as object);
    }
}
