namespace InferHub.Node.Control;

/// <summary>One line of the node's log, as the control API serves it (phase 112, D5).</summary>
public sealed record LogLine(long Seq, DateTimeOffset At, string Level, string Category, string Message, string? Exception);

/// <summary>
/// The node's own log, the last <c>Control:LogLines</c> lines, in memory (phase 112, D5) — what the console shows,
/// for an app that has no console to read. Nothing new is logged for it; rule 7 holds because the node already logs
/// no content.
/// </summary>
public sealed class NodeLogRing(int capacity = 2000)
{
    private readonly object gate = new();
    private readonly LinkedList<LogLine> lines = new();
    private long sequence;
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Add(LogLevel level, string category, string message, Exception? exception)
    {
        TaskCompletionSource wake;

        lock (gate)
        {
            lines.AddLast(new LogLine(++sequence, DateTimeOffset.UtcNow, level.ToString(), category, message, exception?.ToString()));

            while (lines.Count > Math.Max(100, capacity))
            {
                lines.RemoveFirst();
            }

            wake = signal;
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        wake.TrySetResult();
    }

    /// <summary>The lines after <paramref name="seq"/> still held, oldest first, at most <paramref name="max"/> of the newest.</summary>
    public IReadOnlyList<LogLine> After(long seq, int max = 500)
    {
        lock (gate)
        {
            return [.. lines.Where(l => l.Seq > seq).TakeLast(Math.Clamp(max, 1, 5000))];
        }
    }

    public async Task WaitAsync(long seq, CancellationToken cancellationToken)
    {
        Task next;

        lock (gate)
        {
            if (sequence > seq)
            {
                return;
            }

            next = signal.Task;
        }

        await next.WaitAsync(cancellationToken);
    }
}

/// <summary>The logger provider that feeds <see cref="NodeLogRing"/>; Information and above, as the console does by default.</summary>
public sealed class NodeLogRingProvider(NodeLogRing ring) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(ring, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(NodeLogRing ring, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The HTTP client's per-request lines are the noise a reader of this log does not want, as the console's
        // filters already say for the colibri and Strata engines' own probes.
        // So are ASP.NET's per-request lines: the app polls the status every few seconds, and a log made of its own
        // polling would bury what the node did.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Information
            && !(logLevel < LogLevel.Warning
                 && (category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)
                     || category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                ring.Add(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
