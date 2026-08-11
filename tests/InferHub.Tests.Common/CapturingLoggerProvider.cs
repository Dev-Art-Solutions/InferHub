using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace InferHub.Tests;

/// <summary>
/// Collects every formatted log line, so a test can assert on what an operator would actually
/// read. Used by the phase-41 suite to prove a worker's stderr reaches the node's log — and
/// available from phase 42 for the privacy assertion, which needs the opposite question:
/// that a known phrase appears <em>nowhere</em>.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> lines = new();

    public IReadOnlyList<string> Lines => lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, lines);

    public bool Contains(string fragment) =>
        lines.Any(line => line.Contains(fragment, StringComparison.Ordinal));

    public void Dispose()
    {
    }

    private sealed class Capturing(string category, ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            sink.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)}");

            if (exception is not null)
            {
                sink.Enqueue(exception.ToString());
            }
        }
    }
}
