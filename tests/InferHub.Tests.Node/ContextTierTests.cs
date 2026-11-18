using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Backends;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>Phase 153: a long context on the slot, in RAM and on disk, against a llama-server stand-in over a socket.</summary>
public sealed class ContextTierTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-ctx-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLlamaServer server = new();

    private static readonly string DocA = string.Concat(Enumerable.Repeat("War and peace. ", 60));
    private static readonly string DocB = string.Concat(Enumerable.Repeat("Anna Karenina. ", 60));

    public async Task InitializeAsync() => await server.StartAsync(directory);

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public async Task AColdLongChatWritesTheCleanPrefixBeforeItIsAnsweredAndNothingMore()
    {
        var tiers = Tiers();

        await Chat(tiers, Conversation(DocA, "What is it about?"));

        var state = tiers.State();
        Assert.Equal((0L, 0L, 0L, 1L, 1L), (state.Counts.Slot, state.Counts.Ram, state.Counts.Disk, state.Counts.Miss, state.Counts.Saved));

        // The prefix is what both renderings share: up to the start of the last user message, never the question.
        var expected = Render(Conversation(DocA, "\u0001")).IndexOf('\u0001');
        Assert.Equal(expected, Assert.Single(server.Prefills));
        var entry = Assert.Single(state.Entries);
        Assert.Equal(expected, entry.Tokens);
        Assert.True(File.Exists(Path.Combine(directory, $"ctx-{entry.Id}.bin")));

        // The prefill went before the answer, so the answer extended it.
        Assert.Equal(["completion", "save", "chat"], server.Calls.Where(c => c is "completion" or "save" or "chat").ToArray());
    }

    [Fact]
    public async Task TheSameConversationGoingOnIsTheSlotAndIsNotWrittenAgainUntilItGrows()
    {
        var tiers = Tiers();
        await Chat(tiers, Conversation(DocA, "What is it about?"));
        await Chat(tiers, [.. Conversation(DocA, "What is it about?"), Message("assistant", "War."), Message("user", "And peace?")]);

        var state = tiers.State();
        Assert.Equal(1, state.Counts.Slot);
        Assert.Equal(1, state.Counts.Saved);
    }

    [Fact]
    public async Task ASwitchBackFindsTheFirstConversationInRam()
    {
        var tiers = Tiers();
        await Chat(tiers, Conversation(DocA, "What is it about?"));
        await Chat(tiers, Conversation(DocB, "What is it about?"));
        await Chat(tiers, Conversation(DocA, "Who wrote it?"));

        var state = tiers.State();
        Assert.Equal((1L, 2L, 0L), (state.Counts.Ram, state.Counts.Miss, state.Counts.Disk));
        Assert.Empty(server.Restores);
    }

    [Fact]
    public async Task AfterARestartTheConversationComesBackFromDisk()
    {
        await Chat(Tiers(), Conversation(DocA, "What is it about?"));
        var saved = Assert.Single(server.Saves);

        // A new process: no slot, no RAM, only the files and the index beside them.
        var again = Tiers();
        await Chat(again, Conversation(DocA, "Who wrote it?"));

        Assert.Equal(saved, Assert.Single(server.Restores));
        var state = again.State();
        Assert.Equal((1L, 0L), (state.Counts.Disk, state.Counts.Miss));
        Assert.Equal(1, Assert.Single(state.Entries).Uses);
        Assert.NotNull(state.Counts.LastRestoreMs);
    }

    [Fact]
    public async Task ARestartedEngineHoldsNothingSoTheNodeGoesToDiskInsteadOfBelievingTheSlot()
    {
        var launches = 1;
        var tiers = Tiers(launches: () => launches);
        await Chat(tiers, Conversation(DocA, "What is it about?"));
        await Chat(tiers, Conversation(DocB, "What is it about?"));

        launches = 2;
        await Chat(tiers, Conversation(DocB, "Who wrote it?"));
        await Chat(tiers, Conversation(DocA, "Who wrote it?"));

        var state = tiers.State();
        Assert.Equal((0L, 0L, 2L), (state.Counts.Slot, state.Counts.Ram, state.Counts.Disk));
        Assert.Equal(2, server.Restores.Count);
    }

    [Fact]
    public async Task AFullDiskLetsTheLeastUsedFileGoAndNeverAPinnedOne()
    {
        // Room for one prefix only (the stand-in writes 4 bytes a token).
        var tiers = Tiers(diskBytes: 4 * DocA.Length * 3 / 2);
        await Chat(tiers, Conversation(DocA, "What is it about?"));
        var first = Assert.Single(tiers.State().Entries).Id;
        Assert.True(tiers.Index.SetPinned(first, true));

        await Chat(tiers, Conversation(DocB, "What is it about?"));
        Assert.Equal(1, tiers.State().Counts.Failed);
        Assert.Contains("over", tiers.State().LastError);
        Assert.Equal(first, Assert.Single(tiers.State().Entries).Id);

        Assert.True(tiers.Index.SetPinned(first, false));
        await Chat(tiers, Conversation(DocB, "Who wrote it?"));
        var kept = Assert.Single(tiers.State().Entries);
        Assert.NotEqual(first, kept.Id);
        Assert.False(File.Exists(Path.Combine(directory, $"ctx-{first}.bin")));
        Assert.Equal(1, tiers.State().Counts.Evicted);
    }

    [Fact]
    public async Task APictureIsSentUntouched()
    {
        var tiers = Tiers();
        var messages = Conversation(DocA, "What is in the picture?");
        messages[^1]["images"] = new JsonArray("iVBORw0KGgo=");

        await Chat(tiers, messages);

        Assert.DoesNotContain("apply-template", server.Calls);
        Assert.Equal(0, tiers.State().Counts.Miss);
    }

    [Fact]
    public void AStrayFileIsSweptAndAnEntryWithoutItsFileIsDropped()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "ctx-stray.bin"), "x");
        File.WriteAllText(Path.Combine(directory, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(directory, "index.json"), """[{"id":"gone","chars":1,"opening":"a","hash":"a","tokens":1,"bytes":1}]""");

        var index = new ContextTierIndex(directory);

        Assert.Empty(index.All());
        Assert.False(File.Exists(Path.Combine(directory, "ctx-stray.bin")));
        Assert.True(File.Exists(Path.Combine(directory, "keep.txt")));
    }

    [Fact]
    public void TheEngineIsLaunchedWithOneSlotAndTheTwoCachesAndOnlyThen()
    {
        var engine = new EngineOptions { Type = "llamacpp", Serve = { Model = "m.gguf", Context = { Enabled = true, RamMiB = 32768 } } };

        var on = LlamaCppServe.StartInfo(engine, contextDirectory: directory).ArgumentList.ToArray();
        Assert.Contains("--slot-save-path", on);
        Assert.Equal(directory, on[Array.IndexOf(on, "--slot-save-path") + 1]);
        Assert.Equal("32768", on[Array.IndexOf(on, "--cache-ram") + 1]);
        Assert.Equal("1", on[Array.IndexOf(on, "-np") + 1]);
        Assert.True(Directory.Exists(directory));

        engine.Serve.Context.Enabled = false;
        Assert.DoesNotContain("--slot-save-path", LlamaCppServe.StartInfo(engine, contextDirectory: directory).ArgumentList);
    }

    [Fact]
    public void ARouterOrAFlagTheNodeOwnsIsRefusedNamingTheKey()
    {
        var router = new EngineOptions { Type = "llamacpp", Serve = { ModelsDir = "models", Context = { Enabled = true } } };
        Assert.Contains(router.Serve.Context.Problems("Backend:Engines:x", router.Serve), p => p.Contains("needs Serve:Model"));

        var owned = new EngineOptions { Type = "llamacpp", Serve = { Model = "m.gguf", Arguments = ["--cache-ram", "100"], Context = { Enabled = true } } };
        Assert.Contains(owned.Serve.Context.Problems("Backend:Engines:x", owned.Serve), p => p.Contains("--cache-ram"));

        var off = new EngineOptions { Type = "llamacpp", Serve = { ModelsDir = "models", Arguments = ["-np", "4"] } };
        Assert.Empty(off.Serve.Context.Problems("Backend:Engines:x", off.Serve));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private ContextTiers Tiers(long? diskBytes = null, Func<int>? launches = null) => new(
        "ornith",
        new ContextTierOptions
        {
            Enabled = true,
            MinPrefixTokens = 100,
            SaveEveryTokens = 100,
            RamMiB = 64,
            DiskMaxGiB = (diskBytes ?? 1L << 30) / (double)(1L << 30)
        },
        directory,
        () => new HttpClient { BaseAddress = server.Root },
        TimeProvider.System,
        NullLogger.Instance)
    {
        Launches = launches
    };

    private async Task Chat(ContextTiers tiers, JsonObject[] messages)
    {
        var json = new JsonObject { ["model"] = "ornith", ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m.DeepClone()).ToArray()) }.ToJsonString();
        await tiers.RunAsync(json, async () =>
        {
            server.Calls.Enqueue("chat");
            await server.ChatAsync(Render(messages));
            return "{}";
        }, CancellationToken.None);
    }

    private static JsonObject[] Conversation(string document, string question) =>
        [Message("system", document), Message("user", question)];

    private static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>The stand-in's template: concatenative, like ChatML.</summary>
    internal static string Render(IEnumerable<JsonObject> messages) =>
        string.Concat(messages.Select(m => $"<{m["role"]}>{m["content"]}</>")) + "<assistant>";

    /// <summary>
    /// A llama-server with one slot, a concatenative template, one token per character, and slot files it writes into
    /// the directory it was given — 4 bytes a token.
    /// </summary>
    private sealed class FakeLlamaServer : IAsyncDisposable
    {
        private WebApplication? app;
        private string directory = "";
        private int[] slot = [];

        public Uri Root { get; private set; } = new("http://127.0.0.1/");

        public ConcurrentQueue<string> Calls { get; } = new();

        public List<int> Prefills { get; } = [];

        public List<string> Saves { get; } = [];

        public List<string> Restores { get; } = [];

        public async Task StartAsync(string slotDirectory)
        {
            directory = slotDirectory;
            Directory.CreateDirectory(directory);
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            app = builder.Build();

            app.MapPost("/apply-template", async (HttpContext context) =>
            {
                Calls.Enqueue("apply-template");
                var body = await JsonNode.ParseAsync(context.Request.Body);
                var messages = body!["messages"]!.AsArray().Select(m => new JsonObject { ["role"] = m!["role"]!.GetValue<string>(), ["content"] = m["content"]!.GetValue<string>() });
                return Results.Json(new { prompt = Render(messages) });
            });
            app.MapPost("/tokenize", async (HttpContext context) =>
            {
                var body = await JsonNode.ParseAsync(context.Request.Body);
                return Results.Json(new { tokens = body!["content"]!.GetValue<string>().Select(c => (int)c).ToArray() });
            });
            app.MapPost("/detokenize", async (HttpContext context) =>
            {
                var body = await JsonNode.ParseAsync(context.Request.Body);
                return Results.Json(new { content = new string(body!["tokens"]!.AsArray().Select(t => (char)t!.GetValue<int>()).ToArray()) });
            });
            app.MapPost("/completion", async (HttpContext context) =>
            {
                Calls.Enqueue("completion");
                var body = await JsonNode.ParseAsync(context.Request.Body);
                slot = body!["prompt"]!.AsArray().Select(t => t!.GetValue<int>()).ToArray();
                Prefills.Add(slot.Length);
                return Results.Json(new { content = "" });
            });
            app.MapPost("/slots/0", async (HttpContext context, string action) =>
            {
                var body = await JsonNode.ParseAsync(context.Request.Body);
                var file = body!["filename"]!.GetValue<string>();
                var path = Path.Combine(directory, file);

                if (action == "save")
                {
                    Calls.Enqueue("save");
                    Saves.Add(file);
                    await File.WriteAllBytesAsync(path, new byte[slot.Length * 4]);
                    return Results.Json(new { id_slot = 0, filename = file, n_saved = slot.Length, n_written = slot.Length * 4L });
                }

                Calls.Enqueue("restore");
                Restores.Add(file);
                var bytes = new FileInfo(path).Length;
                slot = new int[bytes / 4];
                return Results.Json(new { id_slot = 0, filename = file, n_restored = slot.Length, n_read = bytes });
            });

            await app.StartAsync();
            Root = new Uri(app.Urls.First() + "/");
        }

        public Task ChatAsync(string prompt)
        {
            slot = prompt.Select(c => (int)c).ToArray();
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }
}
