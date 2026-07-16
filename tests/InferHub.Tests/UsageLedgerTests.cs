using System.Threading.Channels;
using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

public class UsageLedgerTests
{
    private static readonly ResolvedClient Acme = new("acme", null);

    // ---- the meter: blocking -------------------------------------------------------------

    [Fact]
    public async Task ABlockingResponseRecordsItsCounts()
    {
        var ledger = new InMemoryUsageLedger();
        var meter = Meter(ledger);

        meter.RecordResponse(
            Acme,
            "chat",
            "llama3",
            """{"model":"llama3","done":true,"prompt_eval_count":17,"eval_count":25}""",
            fallback: false);

        var row = Assert.Single(await ledger.QueryAsync(new UsageQuery()));
        Assert.Equal("acme", row.ClientId);
        Assert.Equal("llama3", row.Model);
        Assert.Equal(1, row.Requests);
        Assert.Equal(17, row.PromptTokens);
        Assert.Equal(25, row.CompletionTokens);
        Assert.Equal(0, row.FallbackRequests);
    }

    [Fact]
    public async Task AResponseWithoutCountsRecordsNothing()
    {
        // No counts means nothing to meter — a zero-token row would only pad the invoice.
        var ledger = new InMemoryUsageLedger();

        Meter(ledger).RecordResponse(Acme, "chat", "llama3", """{"done":true}""", fallback: false);

        Assert.Empty(await ledger.QueryAsync(new UsageQuery()));
    }

    [Fact]
    public async Task AFallbackResponseIsFlaggedAsSuch()
    {
        // A hosted-provider call is the one that costs actual money; the flag is the bill's
        // most important column.
        var ledger = new InMemoryUsageLedger();

        Meter(ledger).RecordResponse(
            Acme,
            "chat",
            "llama3",
            """{"prompt_eval_count":5,"eval_count":7,"done":true}""",
            fallback: true);

        var row = Assert.Single(await ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(1, row.FallbackRequests);
    }

    // ---- the meter: streaming ------------------------------------------------------------

    [Fact]
    public async Task AStreamRecordsTheTerminalChunksCounts()
    {
        var ledger = new InMemoryUsageLedger();
        var meter = Meter(ledger);
        var source = Channel.CreateUnbounded<InferenceChunk>();
        var jobId = Guid.NewGuid();

        var wrapped = meter.WrapStream(source.Reader, Acme, "chat", "llama3", fallback: false, lease: null);

        source.Writer.TryWrite(new InferenceChunk(jobId, """{"message":{"content":"hi"},"done":false}""", false));
        source.Writer.TryWrite(new InferenceChunk(jobId, """{"done":true,"prompt_eval_count":11,"eval_count":42}""", true));
        source.Writer.Complete();

        var chunks = await Drain(wrapped);
        Assert.Equal(2, chunks.Count);

        var row = await WaitForRow(ledger);
        Assert.Equal(11, row.PromptTokens);
        Assert.Equal(42, row.CompletionTokens);
    }

    [Fact]
    public async Task AStreamThatNeverDeliversItsTerminalChunkRecordsNothing()
    {
        // The documented choice: Ollama only reports counts in the terminal chunk, so a stream
        // that never delivers it has no numbers to record. A meter that invents numbers is
        // worse than one that under-counts an aborted request.
        var ledger = new InMemoryUsageLedger();
        var meter = Meter(ledger);
        var source = Channel.CreateUnbounded<InferenceChunk>();

        var wrapped = meter.WrapStream(source.Reader, Acme, "chat", "llama3", fallback: false, lease: null);

        source.Writer.TryWrite(new InferenceChunk(Guid.NewGuid(), """{"message":{"content":"hi"},"done":false}""", false));
        source.Writer.Complete(); // mid-stream disconnect: the done chunk never arrives

        await Drain(wrapped);
        await Task.Delay(100);

        Assert.Empty(await ledger.QueryAsync(new UsageQuery()));
    }

    [Fact]
    public async Task AStreamReleasesTheConcurrencyLeaseHoweverItEnds()
    {
        var meter = Meter(new InMemoryUsageLedger());
        var source = Channel.CreateUnbounded<InferenceChunk>();
        var lease = new TrackingLease();

        var wrapped = meter.WrapStream(source.Reader, Acme, "chat", "llama3", fallback: false, lease);

        source.Writer.TryWrite(new InferenceChunk(Guid.NewGuid(), """{"done":false}""", false));
        source.Writer.Complete();

        await Drain(wrapped);
        await WaitFor(() => lease.Disposed);

        Assert.True(lease.Disposed);
    }

    [Fact]
    public async Task AStreamFeedsTheAdmissionWindowsOnCompletion()
    {
        // Token windows are fed from what was actually generated — the admission side of the
        // same terminal chunk.
        var admission = new AdmissionControl();
        var limited = new ResolvedClient("acme", new ClientLimits { TokensPerDay = 1 });
        Assert.True(admission.TryAdmit(limited, "llama3").Allowed); // creates the state

        var meter = Meter(new InMemoryUsageLedger(), admission);
        var source = Channel.CreateUnbounded<InferenceChunk>();
        var wrapped = meter.WrapStream(source.Reader, limited, "chat", "llama3", fallback: false, lease: null);

        source.Writer.TryWrite(new InferenceChunk(Guid.NewGuid(), """{"done":true,"prompt_eval_count":3,"eval_count":4}""", true));
        source.Writer.Complete();
        await Drain(wrapped);

        await WaitFor(() => admission.LiveUsageOf("acme").TokensToday == 7);
        Assert.Equal(7, admission.LiveUsageOf("acme").TokensToday);
    }

    // ---- embeddings ------------------------------------------------------------------------

    [Fact]
    public async Task AnEmbedResponseRecordsPromptTokensOnly()
    {
        var ledger = new InMemoryUsageLedger();

        Meter(ledger).RecordEmbedResponse(
            Acme,
            "nomic-embed-text",
            """{"model":"nomic-embed-text","embeddings":[[0.1]],"prompt_eval_count":9}""");

        var row = Assert.Single(await ledger.QueryAsync(new UsageQuery()));
        Assert.Equal(9, row.PromptTokens);
        Assert.Equal(0, row.CompletionTokens);
    }

    // ---- the in-memory ledger's query ------------------------------------------------------

    [Fact]
    public async Task QueryFiltersByWindowClientAndModel()
    {
        var ledger = new InMemoryUsageLedger();
        var at = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

        await ledger.RecordAsync(new UsageRecord("acme", "llama3", "chat", 10, 20, false, at));
        await ledger.RecordAsync(new UsageRecord("acme", "llama3", "chat", 1, 2, false, at.AddHours(2)));
        await ledger.RecordAsync(new UsageRecord("acme", "mistral", "chat", 5, 5, false, at));
        await ledger.RecordAsync(new UsageRecord("globex", "llama3", "chat", 7, 7, false, at));

        var windowed = await ledger.QueryAsync(new UsageQuery(at.AddHours(-1), at.AddHours(1)));
        Assert.Equal(3, windowed.Count);

        var acmeLlama = Assert.Single(await ledger.QueryAsync(new UsageQuery(ClientId: "acme", Model: "llama3")));
        Assert.Equal(2, acmeLlama.Requests);
        Assert.Equal(11, acmeLlama.PromptTokens);
        Assert.Equal(22, acmeLlama.CompletionTokens);
    }

    [Fact]
    public async Task NoPromptOrCompletionTextExistsAnywhereInTheUsagePath()
    {
        // Definition of done: grep for it. UsageRecord's shape *is* the whole data model —
        // this asserts the record type has no property that could carry content.
        var properties = typeof(UsageRecord).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(
            new[] { "ClientId", "Model", "Kind", "PromptTokens", "CompletionTokens", "Fallback", "AtUtc", "TotalTokens" }
                .OrderBy(n => n),
            properties.OrderBy(n => n));

        // And every string-typed property is an identifier, not content.
        var stringProps = typeof(UsageRecord).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .OrderBy(n => n);
        Assert.Equal(new[] { "ClientId", "Kind", "Model" }, stringProps);
        await Task.CompletedTask;
    }

    // ---- harness -----------------------------------------------------------------------

    private static UsageMeter Meter(IUsageLedger ledger, AdmissionControl? admission = null)
        => new(ledger, admission ?? new AdmissionControl(), NullLogger<UsageMeter>.Instance);

    private static async Task<List<InferenceChunk>> Drain(ChannelReader<InferenceChunk> reader)
    {
        var chunks = new List<InferenceChunk>();
        await foreach (var chunk in reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    private static async Task<UsageAggregate> WaitForRow(IUsageLedger ledger)
    {
        // The meter records on a background pump; give it a moment without guessing wrong.
        for (var i = 0; i < 100; i++)
        {
            var rows = await ledger.QueryAsync(new UsageQuery());
            if (rows.Count > 0)
            {
                return rows[0];
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("usage record never arrived");
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    private sealed class TrackingLease : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
