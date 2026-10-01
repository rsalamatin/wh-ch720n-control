using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Diagnostics;

// Every entry goes to the UI journal synchronously and to the log file from a background task,
// so logging on the UI thread never waits for the disk.
public sealed class JournalLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    // Bounded so a stalled disk can't grow memory without limit; the oldest unwritten lines go first.
    private const int MaxPendingLines = 10_000;
    // A full file is cleared and logging starts over in it; no second file is kept.
    private const int MaxFileBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding LogEncoding = new(false);

    private readonly DiagnosticsJournal _journal;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<PendingItem> _pending;
    private readonly Task _writerTask;
    private int _disposed;

    // A null logFilePath logs to the journal only.
    public JournalLoggerProvider(DiagnosticsJournal journal, string? logFilePath, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        _journal = journal;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pending = Channel.CreateBounded<PendingItem>(new BoundedChannelOptions(MaxPendingLines)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _writerTask = logFilePath is null
            ? DrainWithoutFileAsync()
            : Task.Run(() => WriteToFileAsync(logFilePath));
    }

    public static string DefaultLogFilePath => Path.Combine(AppContext.BaseDirectory, "headphone-control.log");

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);
        return new JournalLogger(this, categoryName);
    }

    // For crash handlers, where the process may end right after logging. False when the timeout elapsed first.
    public bool Flush(TimeSpan timeout)
    {
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _pending.Writer.TryWrite(new PendingItem(null, flushed)) && flushed.Task.Wait(timeout);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pending.Writer.TryComplete();
        // Synchronous dispose (logging infrastructure) gets a bounded wait so shutdown can't hang on a stuck disk.
        _writerTask.Wait(DrainTimeout);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pending.Writer.TryComplete();
        await _writerTask.WaitAsync(DrainTimeout).ConfigureAwait(false);
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder()
            .Append(_timeProvider.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(ShortLevel(level)).Append("] ")
            .Append(ShortCategory(category)).Append(": ")
            .Append(message);
        if (exception is not null)
        {
            builder.AppendLine().Append(exception);
        }

        var line = builder.ToString();
        _journal.Append(line);
        _pending.Writer.TryWrite(new PendingItem(line, null));
    }

    private async Task DrainWithoutFileAsync()
    {
        await foreach (var item in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // No file configured: the journal already has the line.
            item.Flushed?.TrySetResult();
        }
    }

    private async Task WriteToFileAsync(string path)
    {
        try
        {
            var writer = OpenLogFile(path);
            try
            {
                var newLineBytes = LogEncoding.GetByteCount(writer.NewLine);
                var fileBytes = writer.BaseStream.Length;
                while (await _pending.Reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    var flushRequests = new List<TaskCompletionSource>();
                    try
                    {
                        while (_pending.Reader.TryRead(out var item))
                        {
                            if (item.Line is not null)
                            {
                                var line = FitToEmptyFile(item.Line, newLineBytes);
                                var lineBytes = LogEncoding.GetByteCount(line) + newLineBytes;
                                if (fileBytes > 0 && fileBytes + lineBytes > MaxFileBytes)
                                {
                                    await ClearAsync(writer).ConfigureAwait(false);
                                    fileBytes = 0;
                                }

                                await writer.WriteLineAsync(line).ConfigureAwait(false);
                                fileBytes += lineBytes;
                            }

                            if (item.Flushed is not null)
                            {
                                flushRequests.Add(item.Flushed);
                            }
                        }

                        // Flush per batch so the file is useful even if the process is killed.
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        // Also on failure, so a crash handler's Flush doesn't wait out its timeout.
                        flushRequests.ForEach(request => request.TrySetResult());
                    }
                }
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The logger can't log its own failure to the file; surface it in the UI journal instead.
            _journal.Append(string.Create(CultureInfo.InvariantCulture, $"[ERR] Log file '{path}' is not writable: {ex.Message}"));
            await DrainWithoutFileAsync().ConfigureAwait(false);
        }
    }

    // Not FileMode.Append: a stream opened in Append mode cannot be truncated.
    private static StreamWriter OpenLogFile(string path)
    {
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
        stream.Seek(0, SeekOrigin.End);
        return new StreamWriter(stream, LogEncoding);
    }

    // Truncates through the open handle, so it works while a log viewer has the file open.
    private static async Task ClearAsync(StreamWriter writer)
    {
        await writer.FlushAsync().ConfigureAwait(false);
        writer.BaseStream.SetLength(0);
    }

    // UTF-8 takes at most 3 bytes per UTF-16 char, so the cut line always fits an empty file;
    // an oversized ASCII line therefore keeps only about a third of the cap.
    private static string FitToEmptyFile(string line, int newLineBytes)
    {
        var maxChars = (MaxFileBytes - newLineBytes) / 3;
        if (line.Length <= maxChars || LogEncoding.GetByteCount(line) + newLineBytes <= MaxFileBytes)
        {
            return line;
        }

        return char.IsHighSurrogate(line[maxChars - 1]) ? line[..(maxChars - 1)] : line[..maxChars];
    }

    private static string ShortLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "---",
    };

    private static string ShortCategory(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }

    // Flushed completes once everything queued before it is on disk.
    private readonly record struct PendingItem(string? Line, TaskCompletionSource? Flushed);

    private sealed class JournalLogger(JournalLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
