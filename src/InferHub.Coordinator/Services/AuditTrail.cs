using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace InferHub.Coordinator.Services;

/// <summary>
/// Phase 125. <c>Audit:</c> — a durable trail of who asked this hub for what, which node answered, and when. Off by
/// default; rule 4's eighth exception (125 D5). Not to be confused with <see cref="IAuditLog"/>, the in-memory "last
/// admin action per node" the console has shown since phase 21.
/// </summary>
public sealed class AuditTrailOptions
{
    public const string SectionName = "Audit";

    public bool Enabled { get; set; }

    /// <summary>Relative by default so bare metal and Windows work; the image points it at <c>/data/audit</c>.</summary>
    public string DataDirectory { get; set; } = "./data/audit";

    /// <summary>Whole UTC days kept, today included. Older day files are deleted at start and at each midnight.</summary>
    public int RetentionDays { get; set; } = 90;
}

public sealed class AuditTrailOptionsValidator : IValidateOptions<AuditTrailOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditTrailOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add("Audit:DataDirectory must be set when Audit:Enabled=true.");
        }

        if (options.RetentionDays is < 1 or > 3660)
        {
            failures.Add($"Audit:RetentionDays {options.RetentionDays} is out of range; use 1 to 3660 whole days.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// One line of the trail (125 D1). Who, what was asked of whom, and what came back — never a body, a query string, a
/// header value or a hash of any of them (rule 7). <see cref="Event"/> tells the two shapes apart: <c>request</c> for
/// an HTTP call, <c>node.registered</c> / <c>node.refused</c> / <c>node.disconnected</c> for the fleet's door.
/// </summary>
public sealed record AuditRecord(
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("who")] string Who,
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id = null,
    [property: JsonPropertyName("method"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Method = null,
    [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Status = null,
    [property: JsonPropertyName("ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Ms = null,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonPropertyName("capability"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Capability = null,
    [property: JsonPropertyName("nodes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Nodes = null,
    [property: JsonPropertyName("servedBy"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ServedBy = null,
    [property: JsonPropertyName("node"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Node = null,
    [property: JsonPropertyName("nodeName"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NodeName = null,
    // Phase 126: the class the request ran at, present only while Priority:Enabled.
    [property: JsonPropertyName("priority"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Priority = null,
    // Phase 130: the client's organisation, present only while Organisations:Enabled.
    [property: JsonPropertyName("organisation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Organisation = null)
{
    public const string Request = "request";

    public const string NodeRegistered = "node.registered";

    public const string NodeRefused = "node.refused";

    public const string NodeDisconnected = "node.disconnected";
}

/// <summary>A question asked of the trail (125 D4): a time window, exact-match filters, newest first.</summary>
public sealed record AuditQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    string? Who = null,
    string? Node = null,
    string? Model = null,
    string? Event = null,
    int? Status = null,
    int Limit = AuditTrail.DefaultLimit);

/// <summary>What the trail holds and has done since the hub started. Counts, never names.</summary>
public sealed record AuditTrailSnapshot(long Written, long Dropped, int Days, long Bytes);

/// <summary>
/// What one audited request collected on its way through the hub (125 D2): the model it asked for, and every node a
/// job of it was sent to, in order. Carried by an <see cref="AsyncLocal{T}"/> the middleware sets, so the dispatcher
/// writes into it without any endpoint passing it along — the same shape as phase 106's <c>QuotaScope</c>.
/// </summary>
public sealed class AuditScope
{
    private static readonly AsyncLocal<AuditScope?> current = new();

    private readonly object gate = new();
    private readonly List<string> nodes = [];
    private bool closed;

    public string? Model { get; private set; }

    public string? Capability { get; private set; }

    /// <summary>Phase 126: the request's class, said only while priority is on.</summary>
    public string? Priority { get; private set; }

    internal static AuditScope? Current => current.Value;

    internal static AuditScope Begin()
    {
        var scope = new AuditScope();
        current.Value = scope;
        return scope;
    }

    /// <summary>The request named its model and the kind of work. First answer wins: a retry does not rename it.</summary>
    public static void Asked(string? model, string? capability)
    {
        if (Current is not { } scope)
        {
            return;
        }

        lock (scope.gate)
        {
            if (scope.closed)
            {
                return;
            }

            scope.Model ??= string.IsNullOrWhiteSpace(model) ? null : model;
            scope.Capability ??= string.IsNullOrWhiteSpace(capability) ? null : capability;
        }
    }

    /// <summary>Phase 126: the class the request runs at.</summary>
    public static void Prioritised(string priority)
    {
        if (Current is { } scope)
        {
            lock (scope.gate)
            {
                scope.Priority ??= scope.closed ? null : priority;
            }
        }
    }

    /// <summary>A job of this request was sent to <paramref name="nodeId"/>. A failover appends; nothing is replaced.</summary>
    public static void Dispatched(string nodeId, string? model, string? capability = null)
    {
        if (Current is not { } scope)
        {
            return;
        }

        lock (scope.gate)
        {
            if (scope.closed)
            {
                return;
            }

            scope.nodes.Add(nodeId);
            scope.Model ??= string.IsNullOrWhiteSpace(model) ? null : model;
            scope.Capability ??= string.IsNullOrWhiteSpace(capability) ? null : capability;
        }
    }

    /// <summary>
    /// The record is being written. Anything that arrives later — work a request started and left running, like an
    /// image job's background dispatch — is not this request's line, and is dropped rather than half-reported.
    /// </summary>
    internal IReadOnlyList<string>? Close()
    {
        lock (gate)
        {
            closed = true;
            return nodes.Count > 0 ? nodes.ToArray() : null;
        }
    }
}

/// <summary>
/// Phase 125. The trail: one JSON object per line in <c>audit-yyyy-MM-dd.jsonl</c> (UTC), appended by one background
/// writer so the request path never waits on a disk (125 D3). Day files older than <c>RetentionDays</c> are deleted.
/// </summary>
public sealed class AuditTrail : BackgroundService
{
    public const int DefaultLimit = 100;

    public const int MaxLimit = 1000;

    private const string FilePrefix = "audit-";

    private const string FileSuffix = ".jsonl";

    private const int Capacity = 10_000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly AuditTrailOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<AuditTrail> logger;
    private readonly Channel<AuditRecord> queue = Channel.CreateBounded<AuditRecord>(
        // Wait, not DropWrite: under DropWrite TryWrite answers true for a line it threw away, and a drop nobody counts
        // is a hole in the trail nobody can see. TryWrite never waits under either mode.
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private long written;
    private long dropped;
    private long lastDropWarningTicks;

    public AuditTrail(IOptions<AuditTrailOptions> options, TimeProvider clock, ILogger<AuditTrail> logger)
    {
        this.options = options.Value;
        this.clock = clock;
        this.logger = logger;
    }

    public bool Enabled => options.Enabled;

    public string Directory => Path.GetFullPath(options.DataDirectory);

    public DateTimeOffset Now => clock.GetUtcNow();

    /// <summary>
    /// Hand a record to the writer. Never blocks and never throws into the request it describes: a full queue drops the
    /// line, counts it (<c>inferhub_audit_dropped_total</c>) and says so in the log at most once a minute (125 D3).
    /// </summary>
    public void Write(AuditRecord record)
    {
        if (!options.Enabled)
        {
            return;
        }

        if (queue.Writer.TryWrite(record))
        {
            return;
        }

        Interlocked.Increment(ref dropped);

        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastDropWarningTicks);

        if (now - last > 60_000 && Interlocked.CompareExchange(ref lastDropWarningTicks, now, last) == last)
        {
            logger.LogWarning(
                "Audit trail queue is full ({Capacity} lines waiting for the disk); {Dropped} lines dropped so far",
                Capacity,
                Interlocked.Read(ref dropped));
        }
    }

    /// <summary>Null when the trail is off, so a hub without it reports the payload and the scrape it reported before.</summary>
    public AuditTrailSnapshot? Snapshot()
    {
        if (!options.Enabled)
        {
            return null;
        }

        var files = DayFiles().ToArray();
        return new AuditTrailSnapshot(
            Interlocked.Read(ref written),
            Interlocked.Read(ref dropped),
            files.Length,
            files.Sum(file => SafeLength(file.Path)));
    }

    /// <summary>
    /// Newest first, from the newest day file in the window backwards, stopping at <see cref="AuditQuery.Limit"/>. A line
    /// that does not parse — the tail of a write the process died during — is skipped, not fatal.
    /// </summary>
    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var from = query.FromUtc?.ToUniversalTime();
        var to = query.ToUtc?.ToUniversalTime();
        var results = new List<AuditRecord>();

        foreach (var (day, path) in DayFiles().OrderByDescending(file => file.Day))
        {
            if (from is { } start && day < DateOnly.FromDateTime(start.UtcDateTime))
            {
                break;
            }

            if (to is { } end && day > DateOnly.FromDateTime(end.UtcDateTime))
            {
                continue;
            }

            var lines = new List<AuditRecord>();

            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    if (Parse(line) is { } record && Matches(record, query, from, to))
                    {
                        lines.Add(record);
                    }
                }
            }

            for (var i = lines.Count - 1; i >= 0 && results.Count < limit; i--)
            {
                results.Add(lines[i]);
            }

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        logger.LogInformation(
            "Audit trail on: one line per audited request and node event in {Directory}, kept {Days} days",
            Directory,
            options.RetentionDays);

        DateOnly? openDay = null;
        FileStream? file = null;

        try
        {
            Sweep(Today());

            while (await WaitAsync(stoppingToken))
            {
                while (queue.Reader.TryRead(out var record))
                {
                    var day = DateOnly.FromDateTime(record.At.UtcDateTime);

                    if (file is null || openDay != day)
                    {
                        if (file is not null)
                        {
                            await file.DisposeAsync();
                        }

                        file = Open(day);
                        openDay = day;
                        Sweep(Today());
                    }

                    await file.WriteAsync(Line(record), CancellationToken.None);
                    Interlocked.Increment(ref written);
                }

                if (file is not null)
                {
                    await file.FlushAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Phase 28's lesson, once more: a directory the process cannot write is a deployment error the operator must
            // see, not a request failure. The hub keeps serving; the dropped counter climbs from here on.
            logger.LogError(ex, "Audit trail cannot write to {Directory}; audited lines are being dropped", Directory);
            queue.Writer.TryComplete();
        }
        finally
        {
            if (file is not null)
            {
                await file.DisposeAsync();
            }
        }
    }

    // Drains what is already queued even when the host is stopping, so a clean shutdown loses nothing.
    private async Task<bool> WaitAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await queue.Reader.WaitToReadAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return queue.Reader.TryPeek(out _);
        }
    }

    private FileStream Open(DateOnly day)
        => new(PathFor(day), FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);

    internal string PathFor(DateOnly day)
        => Path.Combine(Directory, FilePrefix + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileSuffix);

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>Deletes day files older than the window. Only files this trail named; anything else in the directory is left alone.</summary>
    internal void Sweep(DateOnly today)
    {
        var oldestKept = today.AddDays(1 - options.RetentionDays);

        foreach (var (day, path) in DayFiles())
        {
            if (day >= oldestKept)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                logger.LogInformation("Audit trail: deleted {File}, older than Audit:RetentionDays={Days}", Path.GetFileName(path), options.RetentionDays);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Audit trail: could not delete {File}", path);
            }
        }
    }

    internal IEnumerable<(DateOnly Day, string Path)> DayFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            yield break;
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*" + FileSuffix))
        {
            var name = Path.GetFileName(path);
            var stamp = name[FilePrefix.Length..^FileSuffix.Length];

            if (DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                yield return (day, path);
            }
        }
    }

    internal static byte[] Line(AuditRecord record)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, Json) + "\n");

    internal static AuditRecord? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AuditRecord>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool Matches(AuditRecord record, AuditQuery query, DateTimeOffset? from, DateTimeOffset? to)
        => (from is null || record.At >= from)
            && (to is null || record.At <= to)
            && Same(query.Who, record.Who)
            && Same(query.Event, record.Event)
            && Same(query.Model, record.Model)
            && (query.Node is null
                || Same(query.Node, record.Node)
                || record.Nodes?.Any(node => Same(query.Node, node)) == true)
            && (query.Status is null || record.Status == query.Status);

    private static bool Same(string? wanted, string? actual)
        => wanted is null || string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase);

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
