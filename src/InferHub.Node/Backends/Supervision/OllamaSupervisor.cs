using InferHub.Node.Configuration;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// Watches the node's own Ollama and puts it back when it falls over. A state machine over three
/// seams and a clock, with no I/O of its own — which is what makes the classification, the
/// restart budget and the one-shot install rule testable without killing anything.
/// </summary>
/// <remarks>
/// Registered as a hosted service only when <c>Backend:Type=ollama</c>, the supervisor is enabled,
/// and <c>Ollama:Endpoint</c> is loopback. Nothing on the node's generic path (<c>Worker</c>,
/// <c>InferenceExecutor</c>, <c>IInferenceBackend</c>) knows a supervisor exists — design rule 1.
/// </remarks>
public sealed class OllamaSupervisor : IHostedService, IBackendSupervisor, IDisposable
{
    private const int NotProbed = -1;

    private readonly OllamaSupervisorOptions options;
    private readonly IOllamaProbe probe;
    private readonly IOllamaProcessControl processControl;
    private readonly IOllamaInstaller installer;
    private readonly TimeProvider time;
    private readonly ILogger<OllamaSupervisor> logger;
    private readonly string endpoint;

    private readonly CancellationTokenSource lifetime = new();
    private readonly Queue<DateTimeOffset> restarts = new();

    private Task? loop;
    private int consecutiveFailures;
    private int healthCode = NotProbed;
    private bool budgetExhaustedLogged;
    private bool installAttempted;
    private bool notInstalledLogged;

    public OllamaSupervisor(
        IOptions<OllamaSupervisorOptions> supervisorOptions,
        IOptions<OllamaOptions> ollamaOptions,
        IOllamaProbe probe,
        IOllamaProcessControl processControl,
        IOllamaInstaller installer,
        TimeProvider time,
        ILogger<OllamaSupervisor> logger)
    {
        options = supervisorOptions.Value;
        endpoint = ollamaOptions.Value.Endpoint;
        this.probe = probe;
        this.processControl = processControl;
        this.installer = installer;
        this.time = time;
        this.logger = logger;
    }

    public bool IsSupervising => true;

    public BackendHealth? Health
        => Volatile.Read(ref healthCode) is var code && code == NotProbed ? null : (BackendHealth)code;

    public event Action? Recovered;

    public event Action<BackendHealth>? Restarting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Supervising the local Ollama at {Endpoint}: probing every {ProbeInterval} with a {ProbeTimeout} deadline, restarting after {Threshold} consecutive failures (at most {MaxRestarts} per {RestartWindow}). AutoInstall={AutoInstall}.",
            endpoint,
            options.ProbeInterval,
            options.ProbeTimeout,
            options.UnhealthyThreshold,
            options.MaxRestartAttempts,
            options.RestartWindow,
            options.AutoInstall);

        loop = Task.Run(() => RunAsync(lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        lifetime.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // The first probe is one interval in: a node and its Ollama usually boot together, and
        // reporting a cold start as a fault would be the supervisor's first act.
        using var timer = new PeriodicTimer(options.ProbeInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }

                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Ollama supervision tick failed");
            }
        }
    }

    /// <summary>One pass of the state machine. Internal so the tests can drive it without a loop.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var health = await probe.CheckAsync(cancellationToken);

        if (health is BackendHealth.Healthy)
        {
            MarkHealthy();
            return;
        }

        consecutiveFailures++;

        // A single failed probe decides nothing: a GC pause, a box saturated mid-load, or a
        // laptop waking from sleep is not a wedge.
        if (consecutiveFailures < options.UnhealthyThreshold)
        {
            logger.LogDebug(
                "Ollama probe {Failures}/{Threshold} failed ({Health})",
                consecutiveFailures,
                options.UnhealthyThreshold,
                health);
            return;
        }

        Declare(health);
        await RemedyAsync(health, cancellationToken);
    }

    private async Task RemedyAsync(BackendHealth health, CancellationToken cancellationToken)
    {
        var installation = await processControl.DiscoverAsync(cancellationToken);

        // Install is a diagnosis, not a retry — it fires on "not installed", never on
        // "not answering", and so is deliberately outside the restart ladder and its budget.
        if (installation.Kind is OllamaInstallKind.Missing)
        {
            await HandleMissingAsync(cancellationToken);
            return;
        }

        if (!TryConsumeRestartBudget(out var attempt, out var backoff))
        {
            if (!budgetExhaustedLogged)
            {
                budgetExhaustedLogged = true;
                logger.LogError(
                    "Ollama at {Endpoint} is still {Health} after {MaxRestarts} restart attempts in {RestartWindow}; not restarting it again. Probing continues, so a recovery — a driver settling, or a human fixing it — is still noticed and this node re-reports its models on its own.",
                    endpoint,
                    health,
                    options.MaxRestartAttempts,
                    options.RestartWindow);
            }

            return;
        }

        if (backoff > TimeSpan.Zero)
        {
            await Task.Delay(backoff, time, cancellationToken);
        }

        // In-flight work is not protected, on purpose: an Ollama that has not answered
        // /api/version in three consecutive probes is not going to finish that stream, and
        // waiting for the fleet to drain would let one stuck request pin the node in a broken
        // state forever — the exact failure this supervisor exists to end. The subscriber that
        // owns the in-flight count logs what the restart costs.
        Raise(Restarting, health);

        logger.LogWarning(
            "Restarting Ollama ({Health}) via {Kind} '{Target}' — attempt {Attempt} of {MaxRestarts} in this {RestartWindow} window.",
            health,
            installation.Kind,
            installation.Target,
            attempt,
            options.MaxRestartAttempts,
            options.RestartWindow);

        if (health is BackendHealth.Wedged)
        {
            // Starting a wedged Ollama fails on a port that is already bound, and the log then
            // blames the wrong thing. Stop first.
            var stop = await processControl.StopAsync(installation, cancellationToken);

            if (!stop.Success)
            {
                LogControlFailure("stop", stop, installation);
                return;
            }
        }

        var start = await processControl.StartAsync(installation, cancellationToken);

        if (!start.Success)
        {
            LogControlFailure("start", start, installation);
            return;
        }

        await WaitForReadyAsync(cancellationToken);
    }

    private async Task HandleMissingAsync(CancellationToken cancellationToken)
    {
        if (!options.AutoInstall)
        {
            if (!notInstalledLogged)
            {
                notInstalledLogged = true;
                logger.LogError(
                    "Nothing is answering at {Endpoint} and no Ollama service or binary was found on this machine. Set {Key}=true to have the node install it, or install Ollama yourself — some fleets install by policy and would not thank us for doing it uninvited.",
                    endpoint,
                    $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.AutoInstall)}");
            }

            return;
        }

        // One attempt per process lifetime. A failing install retried on a timer is a machine
        // downloading the same installer every fifteen seconds.
        if (installAttempted)
        {
            return;
        }

        installAttempted = true;

        var result = await installer.InstallAsync(cancellationToken);

        if (!result.Success)
        {
            logger.LogError(
                "Could not install Ollama: {Error} It will not be retried in this process — restart the node once the cause is fixed.",
                result.Error);
            return;
        }

        logger.LogInformation("Ollama was installed. Starting it.");

        var installation = await processControl.DiscoverAsync(cancellationToken);

        if (installation.Kind is OllamaInstallKind.Missing)
        {
            logger.LogError(
                "The Ollama installer reported success but neither a service nor a binary can be found afterwards.");
            return;
        }

        var start = await processControl.StartAsync(installation, cancellationToken);

        if (!start.Success)
        {
            LogControlFailure("start", start, installation);
            return;
        }

        await WaitForReadyAsync(cancellationToken);
    }

    private async Task<bool> WaitForReadyAsync(CancellationToken cancellationToken)
    {
        var started = time.GetUtcNow();
        var deadline = started + options.ReadyTimeout;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (await probe.CheckAsync(cancellationToken) is BackendHealth.Healthy)
            {
                logger.LogInformation(
                    "Ollama answered {Seconds:F1}s after the restart.",
                    (time.GetUtcNow() - started).TotalSeconds);

                MarkHealthy();
                return true;
            }

            if (time.GetUtcNow() >= deadline)
            {
                // Not a hang and not a second restart: the probe loop keeps running and the
                // budget decides whether anything else is tried.
                logger.LogWarning(
                    "Ollama did not answer within {ReadyTimeout} of the restart. Probing continues.",
                    options.ReadyTimeout);

                return false;
            }

            await Task.Delay(options.ProbeTimeout, time, cancellationToken);
        }

        return false;
    }

    private bool TryConsumeRestartBudget(out int attempt, out TimeSpan backoff)
    {
        var now = time.GetUtcNow();

        while (restarts.Count > 0 && now - restarts.Peek() > options.RestartWindow)
        {
            restarts.Dequeue();
        }

        if (restarts.Count >= options.MaxRestartAttempts)
        {
            attempt = 0;
            backoff = TimeSpan.Zero;
            return false;
        }

        restarts.Enqueue(now);
        attempt = restarts.Count;

        // Widen the gap between attempts: 10s, 20s, 40s at the defaults. A supervisor that
        // restarts a server every fifteen seconds never lets a model finish loading, which
        // replaces a fixable outage with an unfixable one.
        backoff = attempt <= 1
            ? TimeSpan.Zero
            : options.RestartBackoff * Math.Pow(2, attempt - 2);

        return true;
    }

    private void Declare(BackendHealth health)
    {
        var previous = Interlocked.Exchange(ref healthCode, (int)health);

        if (previous == (int)health)
        {
            return;
        }

        // Once per transition, not once per probe: a supervisor that logs every fifteen seconds
        // is a supervisor whose logs nobody reads.
        logger.LogWarning(
            "Ollama at {Endpoint} is {Health} after {Failures} consecutive failed probes.",
            endpoint,
            health,
            consecutiveFailures);
    }

    private void MarkHealthy()
    {
        consecutiveFailures = 0;
        budgetExhaustedLogged = false;

        var previous = Interlocked.Exchange(ref healthCode, (int)BackendHealth.Healthy);

        if (previous is NotProbed or (int)BackendHealth.Healthy)
        {
            return;
        }

        logger.LogInformation("Ollama at {Endpoint} is healthy again.", endpoint);

        // The node's model report is what unroutes it and what routes it back, so recovery has
        // to be pushed rather than waited out on the refresh interval.
        Raise(Recovered);
    }

    private void LogControlFailure(string action, ProcessControlResult result, OllamaInstallation installation)
    {
        if (result.AccessDenied)
        {
            // Privileges are a first-class error here, not a stack trace: "Access is denied" out
            // of a hosted service is a support ticket, and a node running as a restricted account
            // simply cannot control a machine-wide service.
            logger.LogError(
                "Cannot {Action} Ollama ({Kind} '{Target}'): {Error} Grant this account the right to control the service, or run the node under one that has it — see deploy/windows/README.md.",
                action,
                installation.Kind,
                installation.Target,
                result.Error);

            return;
        }

        logger.LogError(
            "Could not {Action} Ollama ({Kind} '{Target}'): {Error}",
            action,
            installation.Kind,
            installation.Target,
            result.Error);
    }

    private void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A supervisor subscriber threw");
        }
    }

    private void Raise(Action<BackendHealth>? handler, BackendHealth health)
    {
        try
        {
            handler?.Invoke(health);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A supervisor subscriber threw");
        }
    }
}

/// <summary>
/// Logs, once, why a node that asked for supervision is not getting it. Registered instead of the
/// supervisor when the loopback / backend-type guard rejects the configuration, because silence
/// there reads as "it's on and everything is fine".
/// </summary>
public sealed class OllamaSupervisorDisabledNotice(string reason, ILogger<OllamaSupervisorDisabledNotice> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "{Key} is on, but this node is not supervising anything: {Reason}",
            $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.Enabled)}",
            reason);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
