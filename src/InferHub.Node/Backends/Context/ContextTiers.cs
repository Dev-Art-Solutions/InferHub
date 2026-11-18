using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using InferHub.Shared.Contracts;
using InferHub.Shared.OpenAi;
using OllamaChatRequest = InferHub.Shared.Ollama.ChatRequest;

namespace InferHub.Node.Backends;

/// <summary>
/// Phase 153: one llama.cpp engine's long context on three tiers. The slot (L1) and llama.cpp's prompt cache in RAM (L2)
/// are the engine's own; the node adds the third — slot files on disk — and decides what goes there, what comes back,
/// and what is deleted when the disk budget is full.
/// </summary>
/// <remarks>
/// <para>
/// <b>A file holds a clean prefix, never a slot as a request left it (153 D2, load-bearing).</b> A slot after an answer
/// holds the prompt and the generated tokens; the next turn re-renders that answer (a thinking model's reasoning is
/// stripped), so the restored state is not a prefix of the next prompt. On a hybrid model (ornith: three of every four
/// layers recurrent) that cannot be rolled back and llama.cpp computes everything again — measured: a 34k-token slot
/// restored in 0.35 s, then 35 s of prompt. So before a turn that grew enough, the node renders the conversation twice —
/// as sent, and with the last user message replaced — takes the tokens the two share, prefills exactly those
/// (<c>n_predict: 0</c>), saves that, and only then sends the turn, which extends it.
/// </para>
/// <para>
/// <b>Requests on the engine run one at a time</b> (<c>-np 1</c>): a restore or a save between two requests must not
/// land in the middle of one.
/// </para>
/// </remarks>
public sealed class ContextTiers
{
    private const double DefaultCharsPerToken = 3.5;
    private const long DefaultBytesPerToken = 20 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string engine;
    private readonly ContextTierOptions options;
    private readonly Func<HttpClient> http;
    private readonly TimeProvider time;
    private readonly ILogger logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly LinkedList<(ContextPrefix Prefix, long Bytes)> ram = new();
    private readonly object countsLock = new();

    private ContextPrefix? slot;
    private long slotTokens;
    private double charsPerToken = DefaultCharsPerToken;
    private long bytesPerToken = DefaultBytesPerToken;
    private bool bytesLearned;
    private long slotHits, ramHits, diskHits, misses, saved, evicted, failed;
    private long? lastRestoreMs, lastSaveMs;
    private string? lastError;

    /// <param name="http">A client at the server's root; each call gets a fresh one, so its timeout may be changed.</param>
    public ContextTiers(string engine, ContextTierOptions options, string directory, Func<HttpClient> http, TimeProvider time, ILogger logger)
    {
        this.engine = engine;
        this.options = options;
        this.http = http;
        this.time = time;
        this.logger = logger;
        Index = new ContextTierIndex(directory);
    }

    public string Engine => engine;

    /// <summary>
    /// How many times the engine has been started. A new process holds no slot and no RAM cache, so the node forgets
    /// both when it changes — found live: after a restart the node still counted a conversation as being in the slot.
    /// </summary>
    public Func<int>? Launches { get; init; }

    private int seenLaunches = -1;

    public ContextTierIndex Index { get; }

    public async Task<T> RunAsync<T>(string ollamaJson, Func<Task<T>> send, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var prompt = await PrepareAsync(ollamaJson, cancellationToken);
            var result = await send();
            Served(prompt);
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(
        string ollamaJson,
        Func<IAsyncEnumerable<string>> send,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);

        try
        {
            var prompt = await PrepareAsync(ollamaJson, cancellationToken);

            await foreach (var chunk in send().WithCancellation(cancellationToken))
            {
                yield return chunk;
            }

            Served(prompt);
        }
        finally
        {
            gate.Release();
        }
    }

    public ContextTierState State()
    {
        lock (countsLock)
        {
            return new ContextTierState(
                Index.Location,
                (long)options.RamMiB * 1024 * 1024,
                ram.Sum(r => r.Bytes),
                options.DiskMaxBytes,
                Index.HeldBytes,
                slotTokens,
                new ContextTierCounts(slotHits, ramHits, diskHits, misses, saved, evicted, failed, lastRestoreMs, lastSaveMs),
                Index.All().Select(e => e.Info()).ToArray(),
                lastError);
        }
    }

    /// <summary>
    /// Finds where the conversation's context is, brings it back from disk when that is the only place, and writes a
    /// clean prefix when the conversation has grown enough. Returns the rendered prompt, or null when this request is not
    /// one the tiers handle (pictures, a template the engine would not render) — it is then sent untouched.
    /// </summary>
    private async Task<RenderedChat?> PrepareAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        if (Launches?.Invoke() is { } launches && launches != seenLaunches)
        {
            lock (countsLock)
            {
                slot = null;
                slotTokens = 0;
                ram.Clear();
            }

            seenLaunches = launches;
        }

        RenderedChat? chat;

        try
        {
            chat = await RenderAsync(ollamaJson, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            Fail($"could not render the chat template: {ex.Message}");
            return null;
        }

        if (chat is null)
        {
            return null;
        }

        await FindAsync(chat.Prompt, cancellationToken);

        try
        {
            await SaveIfGrownAsync(chat, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException)
        {
            Fail($"could not write the prefix to disk: {ex.Message}");
        }

        return chat;
    }

    /// <summary>L1 the slot, L2 llama.cpp's RAM cache, L3 a file — the first that holds a prefix of the prompt.</summary>
    private async Task FindAsync(string prompt, CancellationToken cancellationToken)
    {
        if (slot is not null && slot.IsPrefixOf(prompt))
        {
            Count(ref slotHits);
            return;
        }

        if (InRam(prompt))
        {
            Count(ref ramHits);
            Moved(null, 0);
            return;
        }

        if (Index.Find(prompt) is { } entry && entry.Tokens >= 256)
        {
            var started = time.GetTimestamp();

            try
            {
                await SlotAsync("restore", entry.File, cancellationToken);
                Index.Touch(entry, time.GetUtcNow());
                Moved(entry.Prefix, entry.Tokens);
                Count(ref diskHits);

                lock (countsLock)
                {
                    lastRestoreMs = (long)time.GetElapsedTime(started).TotalMilliseconds;
                }

                logger.LogInformation(
                    "Context tiers ({Engine}): restored {Tokens} tokens from disk in {Ms} ms",
                    engine, entry.Tokens, lastRestoreMs);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                // A file the engine will not read (a model swapped under it) is no use to anybody.
                Index.Remove(entry.Id);
                Fail($"could not restore a prefix from disk: {ex.Message}");
            }
        }

        Count(ref misses);
        Moved(null, 0);
    }

    private async Task SaveIfGrownAsync(RenderedChat chat, CancellationToken cancellationToken)
    {
        if (chat.Alternative is null)
        {
            return;
        }

        var estimate = (long)(chat.Prompt.Length / charsPerToken);
        var kept = Index.Find(chat.Prompt);

        if (estimate < options.MinPrefixTokens || (kept is not null && estimate - kept.Tokens < options.SaveEveryTokens))
        {
            return;
        }

        var tokens = await TokenizeAsync(chat.Prompt, cancellationToken);
        var alternative = await TokenizeAsync(chat.Alternative, cancellationToken);
        charsPerToken = Math.Max(1, chat.Prompt.Length / (double)Math.Max(1, tokens.Length));

        // The tokens both renderings share end where the last user message begins: every later prompt of this
        // conversation starts with exactly these.
        var shared = 0;
        while (shared < tokens.Length && shared < alternative.Length && tokens[shared] == alternative[shared])
        {
            shared++;
        }

        if (shared < options.MinPrefixTokens || (kept is not null && shared - kept.Tokens < options.SaveEveryTokens))
        {
            return;
        }

        var text = await DetokenizeAsync(tokens[..shared], cancellationToken);

        if (!chat.Prompt.StartsWith(text, StringComparison.Ordinal))
        {
            // The engine's detokenizer did not give back the text it was given; a prefix found by its characters
            // would then never be found. Nothing written, nothing lost.
            logger.LogDebug("Context tiers ({Engine}): prefix text did not round-trip; not saved", engine);
            return;
        }

        var now = time.GetUtcNow();

        // Before the first file the size per token is a guess (20 KiB; ornith measured 12.8), so the guess only skips a
        // prefill that cannot fit once a real file has taught the number. The file's real size decides below.
        if (bytesLearned && shared * bytesPerToken > options.DiskMaxBytes)
        {
            Fail($"a {shared}-token prefix needs about {shared * bytesPerToken / (1024 * 1024)} MiB, over Serve:Context:DiskMaxGiB");
            return;
        }

        var started = time.GetTimestamp();
        await PrefillAsync(tokens[..shared], cancellationToken);

        var entry = new ContextTierEntry
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            Tokens = shared,
            CreatedUtc = now,
            LastUsedUtc = now
        };

        var written = await SlotAsync("save", entry.File, cancellationToken);
        var prefix = ContextPrefix.Of(chat.Prompt, text.Length);
        Moved(prefix, shared);

        if (written > 0)
        {
            bytesPerToken = Math.Max(1, written / shared);
            bytesLearned = true;
        }

        Count(ref evicted, Index.MakeRoom(options.DiskMaxBytes, written, now));

        if (Index.HeldBytes + written > options.DiskMaxBytes)
        {
            // Too big on its own, or what is left is pinned: the owner's choice outranks this conversation's.
            Index.Discard(entry.File);
            Fail($"a {shared}-token prefix ({written / (1024 * 1024)} MiB) does not fit: over Serve:Context:DiskMaxGiB by {(Index.HeldBytes + written - options.DiskMaxBytes) / (1024 * 1024)} MiB, pinned files counted");
            return;
        }

        entry.Chars = prefix.Chars;
        entry.Opening = prefix.Opening;
        entry.Hash = prefix.Hash;
        entry.Bytes = written;
        Index.Add(entry);
        Count(ref evicted, Index.Supersede(entry, text));

        Count(ref saved);

        lock (countsLock)
        {
            lastSaveMs = (long)time.GetElapsedTime(started).TotalMilliseconds;
        }

        logger.LogInformation(
            "Context tiers ({Engine}): wrote a {Tokens}-token prefix to disk ({MiB} MiB) in {Ms} ms",
            engine, shared, written / (1024 * 1024), lastSaveMs);
    }

    /// <summary>After the answer: the slot now holds this prompt (and the answer after it).</summary>
    private void Served(RenderedChat? chat)
    {
        if (chat is null)
        {
            // Sent untouched: the slot holds something the node cannot describe.
            Moved(null, 0);
            return;
        }

        Moved(ContextPrefix.Of(chat.Prompt, chat.Prompt.Length), (long)(chat.Prompt.Length / charsPerToken));
    }

    /// <summary>
    /// The slot's content changes. llama.cpp keeps what it held in its RAM cache (<c>--cache-ram</c>, LRU); the node keeps
    /// its own reckoning of that cache by size, which is how L2 is told apart from a miss without asking the engine.
    /// </summary>
    private void Moved(ContextPrefix? next, long tokens)
    {
        lock (countsLock)
        {
            // The same conversation going on (a shorter prefix with the same opening) is not a second thing in RAM.
            var continued = slot is not null && next is not null && slot.Opening == next.Opening && slot.Chars <= next.Chars;

            if (slot is not null && options.RamMiB > 0 && !continued)
            {
                ram.AddFirst((slot, slotTokens * bytesPerToken));
                var budget = (long)options.RamMiB * 1024 * 1024;

                while (ram.Count > 0 && ram.Sum(r => r.Bytes) > budget)
                {
                    ram.RemoveLast();
                }
            }

            if (next is not null)
            {
                var stale = ram.FirstOrDefault(r => r.Prefix == next);
                if (stale != default)
                {
                    ram.Remove(stale);
                }
            }

            slot = next;
            slotTokens = tokens;
        }
    }

    private bool InRam(string prompt)
    {
        lock (countsLock)
        {
            var node = ram.First;

            while (node is not null)
            {
                if (node.Value.Prefix.IsPrefixOf(prompt))
                {
                    ram.Remove(node);
                    return true;
                }

                node = node.Next;
            }

            return false;
        }
    }

    // ---- what llama-server is asked -----------------------------------------------------------------------------

    /// <summary>The prompt as llama-server will render it, and the same conversation with its last user message replaced.</summary>
    private async Task<RenderedChat?> RenderAsync(string ollamaJson, CancellationToken cancellationToken)
    {
        var ollama = JsonSerializer.Deserialize<OllamaChatRequest>(ollamaJson, Json)
                     ?? throw new InvalidOperationException("not a chat request");

        if (ollama.Messages is not { Count: > 0 } messages || messages.Any(m => m.Images is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined }))
        {
            // A picture is encoded by a projector outside the text the template renders; the tiers stay out of it.
            return null;
        }

        var request = UpstreamTranslator.ToOpenAiChat(ollama);
        var prompt = await ApplyTemplateAsync(request.Messages, request.Tools, cancellationToken);
        string? alternative = null;

        if (string.Equals(messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            var replaced = ollama.Messages.Select(m => m).ToList();
            replaced[^1] = new InferHub.Shared.Ollama.ChatMessage { Role = "user", Content = "\u0001" };
            var other = UpstreamTranslator.ToOpenAiChat(new OllamaChatRequest { Model = ollama.Model, Messages = replaced, AdditionalProperties = ollama.AdditionalProperties });
            alternative = await ApplyTemplateAsync(other.Messages, other.Tools, cancellationToken);
        }

        return new RenderedChat(prompt, alternative);
    }

    private async Task<string> ApplyTemplateAsync(object messages, object? tools, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["messages"] = JsonSerializer.SerializeToNode(messages, Json) };

        if (tools is not null)
        {
            body["tools"] = JsonSerializer.SerializeToNode(tools, Json);
        }

        var answer = await PostAsync("apply-template", body, cancellationToken);
        return answer["prompt"]?.GetValue<string>() ?? throw new InvalidOperationException("apply-template answered no prompt");
    }

    private async Task<int[]> TokenizeAsync(string text, CancellationToken cancellationToken)
    {
        // As llama-server tokenizes a rendered chat: special tokens parsed, the model's own BOS added.
        var answer = await PostAsync("tokenize", new JsonObject { ["content"] = text, ["add_special"] = true, ["parse_special"] = true }, cancellationToken);
        return answer["tokens"]?.AsArray().Select(t => t!.GetValue<int>()).ToArray() ?? [];
    }

    private async Task<string> DetokenizeAsync(int[] tokens, CancellationToken cancellationToken)
    {
        var answer = await PostAsync("detokenize", new JsonObject { ["tokens"] = new JsonArray(tokens.Select(t => (JsonNode)t).ToArray()) }, cancellationToken);
        return answer["content"]?.GetValue<string>() ?? "";
    }

    private Task PrefillAsync(int[] tokens, CancellationToken cancellationToken) =>
        PostAsync("completion", new JsonObject
        {
            ["prompt"] = new JsonArray(tokens.Select(t => (JsonNode)t).ToArray()),
            ["n_predict"] = 0,
            ["cache_prompt"] = true,
            ["id_slot"] = 0
        }, cancellationToken);

    /// <summary><c>POST /slots/0?action=save|restore</c>; the bytes written or read.</summary>
    private async Task<long> SlotAsync(string action, string file, CancellationToken cancellationToken)
    {
        var answer = await PostAsync($"slots/0?action={action}", new JsonObject { ["filename"] = file }, cancellationToken);
        return answer["n_written"]?.GetValue<long>() ?? answer["n_read"]?.GetValue<long>() ?? 0;
    }

    private async Task<JsonNode> PostAsync(string path, JsonNode body, CancellationToken cancellationToken)
    {
        using var client = http();

        // A prefill of a 600k-token prefix is half an hour of GPU (measured); the caller's token is the bound.
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{path} answered {(int)response.StatusCode}: {Trim(text)}", null, response.StatusCode);
        }

        return JsonNode.Parse(text) ?? new JsonObject();
    }

    private static string Trim(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private void Count(ref long counter, long by = 1)
    {
        lock (countsLock)
        {
            counter += by;
        }
    }

    private void Fail(string message)
    {
        lock (countsLock)
        {
            failed++;
            lastError = message;
        }

        logger.LogWarning("Context tiers ({Engine}): {Message}", engine, message);
    }

    private sealed record RenderedChat(string Prompt, string? Alternative);
}
