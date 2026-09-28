using Microsoft.Extensions.Logging;

namespace HeadphoneControl.Protocol.Tests.Devices;

// Runs OnLog synchronously on the logging thread, so a test can act at a precise point inside an operation.
internal sealed class HookedLoggerFactory : ILoggerFactory
{
    public Action<string>? OnLog { get; set; }

    public ILogger CreateLogger(string categoryName) => new HookedLogger(this);

    public void AddProvider(ILoggerProvider provider) =>
        throw new NotSupportedException("The hooked factory has no providers.");

    public void Dispose()
    {
    }

    private sealed class HookedLogger(HookedLoggerFactory owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.OnLog?.Invoke(formatter(state, exception));
    }
}
