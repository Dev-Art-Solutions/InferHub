using System.Diagnostics;

namespace InferHub.Node.Backends;

/// <summary>
/// One launched engine process that can be started and stopped while the node keeps running
/// (phase 95, D3). <c>ColibriServe</c>'s loop — relaunch on exit, back off from 2 s to 60 s, reset
/// once a child has stayed up a minute, kill the whole tree — with the one thing a hosted service
/// does not have: a stop that is not the host shutting down.
/// </summary>
/// <remarks>
/// <b>A missing file is one error and no process</b>, as in 93 D5: a model path that is not there
/// would otherwise relaunch a process forever to say the same sentence. The engine reports
/// <c>failed</c> with that sentence, and a later start tries again — the operator may have mounted it.
/// </remarks>
public sealed class EngineProcess(
    string name,
    Func<ProcessStartInfo> startInfo,
    Func<string?> precondition,
    TimeProvider time,
    ILogger logger) : IAsyncDisposable
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

    private readonly object sync = new();
    private CancellationTokenSource? loop;
    private Task? loopTask;
    private Process? child;

    /// <summary>Why it could not be launched, or the last exit. Null while it runs cleanly.</summary>
    public string? LastError { get; private set; }

    /// <summary>The precondition failed: there is nothing to relaunch until somebody fixes the box.</summary>
    public bool Failed { get; private set; }

    public bool IsRunning
    {
        get
        {
            lock (sync)
            {
                return loopTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Starts the supervise loop. Idempotent: a running engine is left alone.</summary>
    public void Start()
    {
        lock (sync)
        {
            if (loopTask is { IsCompleted: false })
            {
                return;
            }

            if (precondition() is { } problem)
            {
                Failed = true;
                LastError = problem;
                logger.LogWarning("Engine '{Engine}' was not started: {Problem}", name, problem);
                return;
            }

            Failed = false;
            LastError = null;
            loop = new CancellationTokenSource();
            var token = loop.Token;
            loopTask = Task.Run(() => SuperviseAsync(token));
        }
    }

    /// <summary>Stops relaunching and kills the process tree. Idempotent.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (sync)
        {
            cts = loop;
            task = loopTask;
            loop = null;
            loopTask = null;
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync();
        Kill();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task SuperviseAsync(CancellationToken token)
    {
        var backoff = FirstBackoff;

        while (!token.IsCancellationRequested)
        {
            var started = time.GetUtcNow();
            int? exitCode;

            try
            {
                exitCode = await RunOnceAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = $"could not start '{startInfo().FileName}': {ex.Message}";
                logger.LogError(ex, "Could not start engine '{Engine}'.", name);
                exitCode = null;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (time.GetUtcNow() - started >= StableAfter)
            {
                backoff = FirstBackoff;
            }

            if (exitCode is not null)
            {
                LastError = $"exited with code {exitCode}";
            }

            logger.LogWarning(
                "Engine '{Engine}' exited ({ExitCode}); starting it again in {Backoff}.",
                name,
                exitCode?.ToString() ?? "did not start",
                backoff);

            try
            {
                await Task.Delay(backoff, time, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken token)
    {
        var info = startInfo();
        var process = Process.Start(info)
            ?? throw new InvalidOperationException("Process.Start returned no process.");

        child = process;

        try
        {
            // Windows: a node killed rather than stopped takes this engine with it.
            ChildProcessJob.Assign(process);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Engine '{Engine}' runs, but is not tied to this node's lifetime; a node killed outright would leave it running.", name);
        }

        logger.LogInformation(
            "Started engine '{Engine}' (pid {Pid}): {File} {Arguments}",
            name,
            process.Id,
            info.FileName,
            string.Join(' ', info.ArgumentList));

        // Pumped, not discarded: a model that fails to load says why on stderr and nowhere else.
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
            return process.ExitCode;
        }
        finally
        {
            if (token.IsCancellationRequested)
            {
                Kill();
            }
            else
            {
                Interlocked.Exchange(ref child, null)?.Dispose();
            }
        }
    }

    private void Log(string? line)
    {
        // Health probes are the node's own and not news; loads, refusals and crashes are.
        if (!string.IsNullOrWhiteSpace(line)
            && !line.Contains("/health", StringComparison.Ordinal))
        {
            logger.LogInformation("{Engine}: {Line}", name, line);
        }
    }

    private void Kill()
    {
        var process = Interlocked.Exchange(ref child, null);

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                // colibri's launcher is Python with the engine as its child; the tree holds the RAM.
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
