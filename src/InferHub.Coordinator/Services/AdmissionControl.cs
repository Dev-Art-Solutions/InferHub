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

    public AdmissionDecision TryAdmit(
        ResolvedClient client,
        string model,
        string unitKind = UsageUnits.Tokens,
        string? secondaryUnitKind = null)
        => TryAdmit(client, model, DateTimeOffset.UtcNow, unitKind, secondaryUnitKind);

    /// <param name="secondaryUnitKind">
    /// The second unit this request will also be metered in, or null (phase 59, D3). A video is
    /// billed in megapixel-steps <em>and</em> in seconds; <c>ImageOutcome</c> has carried both since
    /// phase 57 and until now the gate looked only at the first, so a client whose only limit was a
    /// picture budget spent a clip budget nobody was checking.
    /// </param>
    internal AdmissionDecision TryAdmit(
        ResolvedClient client,
        string model,
        DateTimeOffset now,
        string unitKind = UsageUnits.Tokens,
        string? secondaryUnitKind = null)
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

            // Hard budget first: a client that is out of budget for the day should hear 402,
            // not a 429 that suggests waiting a minute will help.
            //
            // The budget checked is the one for the unit *this* request is measured in (phase-42
            // D7). A chat request cannot exhaust an audio budget and a transcription cannot exhaust
            // a token budget, because neither consumes the other's unit — and a client whose only
            // limit is TokensPerDay would otherwise transcribe a library for free.
            if (ExhaustedBudget(limits, state, unitKind) is { } spent)
            {
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status402PaymentRequired,
                    $"daily {Describe(spent.Unit)} budget of {Format(spent.Daily)} exhausted for client '{client.Id}'",
                    SecondsUntilUtcMidnight(now));
            }

            // The second unit a request is metered in, checked in its own right and named in its own
            // sentence (59 D3). The request's primary unit is checked first on purpose: a caller who
            // is out of both should hear about the one the request is principally measured in.
            if (secondaryUnitKind is not null
                && ExhaustedBudget(limits, state, secondaryUnitKind) is { } secondary)
            {
                return new AdmissionDecision(
                    false,
                    StatusCodes.Status402PaymentRequired,
                    $"daily {Describe(secondary.Unit)} budget of {Format(secondary.Daily)} exhausted for client '{client.Id}'",
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
        => RecordUnits(clientId, UsageUnits.Tokens, tokens, atUtc);

    /// <summary>
    /// The general form (phase 42). Tokens additionally feed the per-minute window; the two audio
    /// units have daily budgets only, because "seconds of audio per minute" is a limit nobody has
    /// asked for and a key nobody would be able to reason about.
    /// </summary>
    public void RecordUnits(string clientId, string unitKind, double units, DateTimeOffset atUtc)
    {
        if (units <= 0 || !states.TryGetValue(clientId, out var state))
        {
            // No state means no limits were ever checked for this client — nothing to feed.
            return;
        }

        lock (state)
        {
            state.Prune(atUtc);

            switch (unitKind)
            {
                case UsageUnits.AudioSeconds:
                    state.AudioSecondsToday += units;
                    break;

                case UsageUnits.Characters:
                    state.CharactersToday += units;
                    break;

                case UsageUnits.MegapixelSteps:
                    state.MegapixelStepsToday += units;
                    break;

                // Phase 57. It has NO limit knob — a quota for a one-model catalogue is a key that
                // is wrong by the time phase 58 lands — but it must have a case, because the default
                // branch below counts whatever reaches it as TOKENS, and five seconds of video
                // silently spending five tokens of somebody's minute budget is the quietest possible
                // wrong number.
                case UsageUnits.VideoSeconds:
                    state.VideoSecondsToday += units;
                    break;

                default:
                    var tokens = (long)units;
                    state.TokenEvents.Enqueue(new TokenEvent(atUtc, tokens));
                    state.TokensLastMinute += tokens;
                    state.TokensToday += tokens;
                    break;
            }
        }
    }

    /// <summary>
    /// One unit's daily budget, when it exists and is already spent. Null covers both "this client
    /// has no limit in that unit" and "there is room left in it", which are the same answer to the
    /// only question the caller is asking.
    /// </summary>
    private static (string Unit, double Daily)? ExhaustedBudget(
        ClientLimits limits,
        ClientState state,
        string unitKind) =>
        DailyBudget(limits, unitKind) is { } daily && Consumed(state, unitKind) >= daily
            ? (unitKind, daily)
            : null;

    private static double? DailyBudget(ClientLimits limits, string unitKind) => unitKind switch
    {
        UsageUnits.AudioSeconds => limits.AudioSecondsPerDay,
        UsageUnits.Characters => limits.CharactersPerDay,
        UsageUnits.MegapixelSteps => limits.MegapixelStepsPerDay,
        UsageUnits.VideoSeconds => limits.VideoSecondsPerDay,
        _ => limits.TokensPerDay
    };

    private static double Consumed(ClientState state, string unitKind) => unitKind switch
    {
        UsageUnits.AudioSeconds => state.AudioSecondsToday,
        UsageUnits.Characters => state.CharactersToday,
        UsageUnits.MegapixelSteps => state.MegapixelStepsToday,
        UsageUnits.VideoSeconds => state.VideoSecondsToday,
        _ => state.TokensToday
    };

    private static string Describe(string unitKind) => unitKind switch
    {
        UsageUnits.AudioSeconds => "audio-second",
        UsageUnits.Characters => "character",
        UsageUnits.MegapixelSteps => "megapixel-step",
        UsageUnits.VideoSeconds => "video-second",
        _ => "token"
    };

    /// <summary>
    /// Invariant, and trimmed of a trailing <c>.0</c>. A budget rendered with the host's culture
    /// puts a decimal comma in an error message on a Bulgarian or German box — the same locale trap
    /// <c>PrometheusFormatter</c> guards against, in a place a test on an English CI runner would
    /// never see.
    /// </summary>
    private static string Format(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

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
                state.TokensToday,
                state.AudioSecondsToday,
                state.CharactersToday,
                state.MegapixelStepsToday,
                state.VideoSecondsToday);
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
        public double AudioSecondsToday;
        public double CharactersToday;
        public double MegapixelStepsToday;

        public double VideoSecondsToday;
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
                AudioSecondsToday = 0;
                CharactersToday = 0;
                MegapixelStepsToday = 0;
                VideoSecondsToday = 0;
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
    long TokensToday,
    double AudioSecondsToday = 0,
    double CharactersToday = 0,
    double MegapixelStepsToday = 0,
    double VideoSecondsToday = 0);
