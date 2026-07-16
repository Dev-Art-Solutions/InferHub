using InferHub.Coordinator.Auth;

namespace InferHub.Coordinator.Services;

/// <summary>
/// The verdict on whether a request may enter the fleet. When it may, <see cref="Lease"/> holds
/// the client's concurrency slot and must be disposed when the request finishes — for a blocking
/// call that is the end of the handler, for a stream it is the end of the stream.
/// </summary>
public sealed record AdmissionDecision(
    bool Allowed,
    int Status = 0,
    string? Message = null,
    int? RetryAfterSeconds = null,
    IDisposable? Lease = null)
{
    public static readonly AdmissionDecision Admitted = new(true);
}

/// <summary>
/// Per-client concurrency, rate and budget enforcement (phase 25). Checked once, in
/// <c>InferenceCore</c>, before routing — not in middleware, because the decision needs the
/// model name. A client with no limits takes a single dictionary miss and is admitted.
///
/// Token consumption is fed back by <see cref="UsageMeter"/> after completion, so a window's
/// token count is what was actually generated, not what was promised. That means a budget can
/// be *overshot* by one in-flight request — accepted: rejecting up front would require trusting
/// a token estimate, and a meter that guesses is worse than one that lags by a request.
/// </summary>
public sealed class AdmissionControl
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ClientState> states =
        new(StringComparer.Ordinal);

    public AdmissionDecision TryAdmit(ResolvedClient client, string model)
        => TryAdmit(client, model, DateTimeOffset.UtcNow);

    internal AdmissionDecision TryAdmit(ResolvedClient client, string model, DateTimeOffset now)
    {
        var limits = client.Limits;

        if (limits is null || !limits.HasAny)
        {
            return AdmissionDecision.Admitted;
        }

        // Outside the allowlist is the same 404 as a model that does not exist — a client is
        // not told that a model exists but is not for them.
        if (limits.AllowedModels is { Count: > 0 }
            && !limits.AllowedModels.Any(allowed =>
                string.Equals(allowed?.Trim(), model, StringComparison.OrdinalIgnoreCase)))
        {
            return new AdmissionDecision(false, StatusCodes.Status404NotFound, $"model '{model}' not found");
        }

        var state = states.GetOrAdd(client.Id, _ => new ClientState());

        lock (state)
        {
            state.Prune(now);

            // Hard budget first: a client that is out of tokens for the day should hear 402,
            // not a 429 that suggests waiting a minute will help.
            if (limits.TokensPerDay is { } daily && state.TokensToday >= daily)
            {
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status402PaymentRequired,
                    $"daily token budget of {daily} exhausted for client '{client.Id}'",
                    SecondsUntilUtcMidnight(now));
            }

            if (limits.MaxConcurrent is { } cap && state.InFlight >= cap)
            {
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status429TooManyRequests,
                    $"client '{client.Id}' is at its concurrency limit of {cap}",
                    RetryAfterSeconds: 1);
            }

            if (limits.RequestsPerMinute is { } rpm && state.RequestTimestamps.Count >= rpm)
            {
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status429TooManyRequests,
                    $"client '{client.Id}' exceeded {rpm} requests per minute",
                    SecondsUntilWindowFrees(state.RequestTimestamps.Peek(), now));
            }

            if (limits.TokensPerMinute is { } tpm && state.TokensLastMinute >= tpm)
            {
                var oldest = state.TokenEvents.Count > 0 ? state.TokenEvents.Peek().AtUtc : now;
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status429TooManyRequests,
                    $"client '{client.Id}' exceeded {tpm} tokens per minute",
                    SecondsUntilWindowFrees(oldest, now));
            }

            state.InFlight++;
            state.RequestTimestamps.Enqueue(now);
        }

        return new AdmissionDecision(true, Lease: new ConcurrencyLease(state));
    }

    /// <summary>Fed by <see cref="UsageMeter"/> when a request completes and its counts are known.</summary>
    public void RecordTokens(string clientId, long tokens, DateTimeOffset atUtc)
    {
        if (tokens <= 0 || !states.TryGetValue(clientId, out var state))
        {
            // No state means no limits were ever checked for this client — nothing to feed.
            return;
        }

        lock (state)
        {
            state.Prune(atUtc);
            state.TokenEvents.Enqueue(new TokenEvent(atUtc, tokens));
            state.TokensLastMinute += tokens;
            state.TokensToday += tokens;
        }
    }

    /// <summary>Live view for <c>GET /api/admin/clients</c>.</summary>
    public ClientLiveUsage LiveUsageOf(string clientId)
    {
        if (!states.TryGetValue(clientId, out var state))
        {
            return new ClientLiveUsage(0, 0, 0, 0);
        }

        lock (state)
        {
            state.Prune(DateTimeOffset.UtcNow);
            return new ClientLiveUsage(
                state.InFlight,
                state.RequestTimestamps.Count,
                state.TokensLastMinute,
                state.TokensToday);
        }
    }

    private static int SecondsUntilWindowFrees(DateTimeOffset oldest, DateTimeOffset now)
    {
        var seconds = (int)Math.Ceiling((oldest.AddMinutes(1) - now).TotalSeconds);
        return Math.Max(1, seconds);
    }

    private static int SecondsUntilUtcMidnight(DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
        return Math.Max(1, (int)Math.Ceiling((midnight - now).TotalSeconds));
    }

    private readonly record struct TokenEvent(DateTimeOffset AtUtc, long Tokens);

    private sealed class ClientState
    {
        public int InFlight;
        public long TokensLastMinute;
        public long TokensToday;
        public DateOnly Day = DateOnly.FromDateTime(DateTime.UtcNow);
        public readonly Queue<DateTimeOffset> RequestTimestamps = new();
        public readonly Queue<TokenEvent> TokenEvents = new();

        /// <summary>Caller holds the lock.</summary>
        public void Prune(DateTimeOffset now)
        {
            var windowStart = now.AddMinutes(-1);

            while (RequestTimestamps.Count > 0 && RequestTimestamps.Peek() < windowStart)
            {
                RequestTimestamps.Dequeue();
            }

            while (TokenEvents.Count > 0 && TokenEvents.Peek().AtUtc < windowStart)
            {
                TokensLastMinute -= TokenEvents.Dequeue().Tokens;
            }

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (today != Day)
            {
                Day = today;
                TokensToday = 0;
            }
        }
    }

    private sealed class ConcurrencyLease(ClientState state) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            lock (state)
            {
                if (state.InFlight > 0)
                {
                    state.InFlight--;
                }
            }
        }
    }
}

public sealed record ClientLiveUsage(
    int InFlight,
    int RequestsLastMinute,
    long TokensLastMinute,
    long TokensToday);
