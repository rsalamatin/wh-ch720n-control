using HeadphoneControl.Diagnostics;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Tests.Diagnostics;

public class JournalLoggerProviderTests
{
    private static string RandomLogPath() =>
        Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", $"{Guid.NewGuid():N}.log");

    [Test]
    public async Task WhenLineIsLoggedThenItIsAppendedToTheFile()
    {
        var path = RandomLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Console.WriteLine($"Log file: {path}");
        var provider = new JournalLoggerProvider(new DiagnosticsJournal(), path);
        provider.CreateLogger("Test.Category").LogInformation("hello file");

        await provider.DisposeAsync();

        await Assert.That(await File.ReadAllTextAsync(path)).Contains("[INF] Category: hello file");
    }

    [Test]
    public async Task WhenFlushedThenLoggedLineIsAlreadyOnDisk()
    {
        var path = RandomLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Console.WriteLine($"Log file: {path}");
        await using var provider = new JournalLoggerProvider(new DiagnosticsJournal(), path);
        provider.CreateLogger("Test").LogCritical("crash");

        provider.Flush(TimeSpan.FromSeconds(5));

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        await Assert.That(await reader.ReadToEndAsync()).Contains("crash");
    }

    [Test]
    public async Task WhenLogFileIsNotWritableThenJournalReportsIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", Guid.NewGuid().ToString("N"), "missing", "x.log");
        var journal = new DiagnosticsJournal();
        var provider = new JournalLoggerProvider(journal, path);
        provider.CreateLogger("Test").LogInformation("still shown");

        await provider.DisposeAsync();

        await Assert.That(journal.Snapshot().Any(line => line.Contains("not writable", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task WhenLogFileIsNotWritableThenLinesStillReachTheJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), "HeadphoneControl.Tests", Guid.NewGuid().ToString("N"), "missing", "x.log");
        var journal = new DiagnosticsJournal();
        var provider = new JournalLoggerProvider(journal, path);

        provider.CreateLogger("Test").LogInformation("still shown");
        await provider.DisposeAsync();

        await Assert.That(journal.Snapshot().Any(line => line.Contains("still shown", StringComparison.Ordinal))).IsTrue();
    }
}
