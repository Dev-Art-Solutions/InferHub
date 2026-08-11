using InferHub.Coordinator.Auth;
using InferHub.Coordinator.Services;
using Microsoft.AspNetCore.Http;

namespace InferHub.Tests;

public class AdmissionControlTests
{
    private static readonly DateTimeOffset Noon = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    // ---- who is limited at all -----------------------------------------------------------

    [Fact]
    public void AnAnonymousClientIsAlwaysAdmitted()
    {
        var admission = new AdmissionControl();

        for (var i = 0; i < 1000; i++)
        {
            Assert.True(admission.TryAdmit(ResolvedClient.Anonymous, "llama3", Noon).Allowed);
        }
    }

    [Fact]
    public void AClientWithEmptyLimitsIsAlwaysAdmitted()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits());

        Assert.True(admission.TryAdmit(client, "llama3", Noon).Allowed);
        Assert.Null(admission.TryAdmit(client, "llama3", Noon).Lease);
    }

    // ---- concurrency -----------------------------------------------------------------------

    [Fact]
    public void TheConcurrencyCapRejectsWith429AndFreesOnLeaseDispose()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { MaxConcurrent = 2 });

        var first = admission.TryAdmit(client, "llama3", Noon);
        var second = admission.TryAdmit(client, "llama3", Noon);
        var third = admission.TryAdmit(client, "llama3", Noon);

        Assert.True(first.Allowed);
        Assert.True(second.Allowed);
        Assert.False(third.Allowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, third.Status);
        Assert.NotNull(third.RetryAfterSeconds);

        first.Lease!.Dispose();

        Assert.True(admission.TryAdmit(client, "llama3", Noon).Allowed);
    }

    [Fact]
    public void OneClientsSaturationDoesNotTouchAnother()
    {
        // Definition of done: one client saturates its quota; the other is unaffected.
        var admission = new AdmissionControl();
        var starving = new ResolvedClient("starving", new ClientLimits { MaxConcurrent = 1 });
        var healthy = new ResolvedClient("healthy", new ClientLimits { MaxConcurrent = 1 });

        Assert.True(admission.TryAdmit(starving, "llama3", Noon).Allowed);
        Assert.False(admission.TryAdmit(starving, "llama3", Noon).Allowed);

        Assert.True(admission.TryAdmit(healthy, "llama3", Noon).Allowed);
    }

    // ---- requests per minute ----------------------------------------------------------------

    [Fact]
    public void RpmRejectsWith429AndAWindowAccurateRetryAfter()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { RequestsPerMinute = 2 });

        admission.TryAdmit(client, "llama3", Noon).Lease!.Dispose();
        admission.TryAdmit(client, "llama3", Noon.AddSeconds(10)).Lease!.Dispose();

        var rejected = admission.TryAdmit(client, "llama3", Noon.AddSeconds(20));

        Assert.False(rejected.Allowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Status);
        // The oldest request leaves the window at Noon+60s; we are at Noon+20s.
        Assert.Equal(40, rejected.RetryAfterSeconds);
    }

    [Fact]
    public void RpmWindowSlides()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { RequestsPerMinute = 1 });

        admission.TryAdmit(client, "llama3", Noon).Lease!.Dispose();
        Assert.False(admission.TryAdmit(client, "llama3", Noon.AddSeconds(30)).Allowed);
        Assert.True(admission.TryAdmit(client, "llama3", Noon.AddSeconds(61)).Allowed);
    }

    // ---- tokens per minute / per day ----------------------------------------------------------

    [Fact]
    public void TpmRejectsWith429OnceTheWindowIsFull()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { TokensPerMinute = 100 });

        admission.TryAdmit(client, "llama3", Noon).Lease!.Dispose();
        admission.RecordTokens("acme", 120, Noon.AddSeconds(1));

        var rejected = admission.TryAdmit(client, "llama3", Noon.AddSeconds(2));
        Assert.False(rejected.Allowed);
        Assert.Equal(StatusCodes.Status429TooManyRequests, rejected.Status);

        // A minute later the window has emptied.
        Assert.True(admission.TryAdmit(client, "llama3", Noon.AddSeconds(65)).Allowed);
    }

    [Fact]
    public void TheDailyBudgetRejectsWith402UntilUtcMidnight()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { TokensPerDay = 1000 });

        admission.TryAdmit(client, "llama3", Noon).Lease!.Dispose();
        admission.RecordTokens("acme", 1000, Noon);

        var rejected = admission.TryAdmit(client, "llama3", Noon.AddMinutes(5));

        Assert.False(rejected.Allowed);
        Assert.Equal(StatusCodes.Status402PaymentRequired, rejected.Status);
        // 402 means "waiting a minute will not help" — Retry-After points at UTC midnight.
        Assert.Equal((int)(new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero) - Noon.AddMinutes(5)).TotalSeconds,
            rejected.RetryAfterSeconds);

        // A new UTC day zeroes the budget.
        Assert.True(admission.TryAdmit(client, "llama3", Noon.AddDays(1)).Allowed);
    }

    [Fact]
    public void TheBudgetIsCheckedBeforeTheRateLimits()
    {
        // A client that is out of budget should hear 402, not a 429 that suggests waiting.
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits
        {
            TokensPerDay = 10,
            RequestsPerMinute = 1
        });

        admission.TryAdmit(client, "llama3", Noon).Lease!.Dispose();
        admission.RecordTokens("acme", 10, Noon);

        var rejected = admission.TryAdmit(client, "llama3", Noon.AddSeconds(1));
        Assert.Equal(StatusCodes.Status402PaymentRequired, rejected.Status);
    }

    // ---- model allowlist ------------------------------------------------------------------

    [Fact]
    public void AModelOutsideTheAllowlistIs404IdenticalToAMissingModel()
    {
        // Not 403: a client is not told that a model exists but is not for them.
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { AllowedModels = ["llama3"] });

        var rejected = admission.TryAdmit(client, "gpt-secret", Noon);

        Assert.False(rejected.Allowed);
        Assert.Equal(StatusCodes.Status404NotFound, rejected.Status);
        Assert.Equal("model 'gpt-secret' not found", rejected.Message);
        Assert.Null(rejected.RetryAfterSeconds);
    }

    [Fact]
    public void TheAllowlistIsCaseInsensitiveAndTrimmed()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { AllowedModels = [" LLaMA3 "] });

        Assert.True(admission.TryAdmit(client, "llama3", Noon).Allowed);
    }

    // ---- live view ----------------------------------------------------------------------

    [Fact]
    public void LiveUsageReportsWindowsAndInFlight()
    {
        var admission = new AdmissionControl();
        var client = new ResolvedClient("acme", new ClientLimits { MaxConcurrent = 5 });

        var lease = admission.TryAdmit(client, "llama3", DateTimeOffset.UtcNow).Lease;
        admission.RecordTokens("acme", 50, DateTimeOffset.UtcNow);

        var live = admission.LiveUsageOf("acme");
        Assert.Equal(1, live.InFlight);
        Assert.Equal(1, live.RequestsLastMinute);
        Assert.Equal(50, live.TokensLastMinute);
        Assert.Equal(50, live.TokensToday);

        lease!.Dispose();
        Assert.Equal(0, admission.LiveUsageOf("acme").InFlight);
    }
}
