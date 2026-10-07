using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends.Colibri;

/// <summary>
/// Launches <c>coli serve</c> as this node's own child when <c>Colibri:Serve:Model</c> is set, and
/// launches it again when it exits (93 D5). Registered only then — the path is the consent.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit is the only trigger.</b> A slow engine is <see cref="ColibriWatcher"/>'s to declare and
/// nobody's to kill (D4). Relaunches back off from 2 s, doubling to 60 s, and the backoff resets once
/// a child has stayed up for a minute — a crash loop costs a log line a minute, not a CPU.
/// </para>
/// <para>
/// <b>A missing model directory is one warning and no process.</b> In the bundled image the path is
/// a default and the mount is the operator's; a container started without it would otherwise
/// restart a Python process forever to report the same missing directory.
/// </para>
/// </remarks>
public sealed class ColibriServe(
    IOptions<ColibriOptions> options,
    TimeProvider time,
    ILogger<ColibriServe> logger) : BackgroundService
{
    /// <summary>
    /// <c>COLI_DEBUG</c> makes the gateway tee the rendered prompt and the decoded output to stderr,
    /// and this class logs that stream. Rule 7 says no prompt reaches a log on any host at any
    /// level, so the child does not inherit it.
    /// </summary>
    public const string ContentTeeVariable = "COLI_DEBUG";

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(1);

    private readonly ColibriOptions options = options.Value;

    private Process? child;

    /// <summary>The whole command line, pure, so the tests can read it without starting Python.</summary>
    public static ProcessStartInfo StartInfo(ColibriOptions options)
        => StartInfo(options, options.Serve.Model!, options.Serve.ResolvedModelId(), options.Serve.Port);

    /// <summary>One catalogue model on its own port (97 D1): the same command line, three values apart.</summary>
    public static ProcessStartInfo StartInfo(ColibriOptions options, string model, string modelId, int port)
    {
        var serve = options.Serve;

        var info = new ProcessStartInfo(serve.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in new[]
                 {
                     serve.Launcher, "serve",
                     "--model", model,
                     "--model-id", modelId,
                     "--host", "127.0.0.1",
                     "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--kv-slots", options.KvSlots.ToString(System.Globalization.CultureInfo.InvariantCulture)
                 })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Remove(ContentTeeVariable);

        return info;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var model = options.Serve.Model!;

        if (!Directory.Exists(model))
        {
            logger.LogWarning(
                "{Key} is '{Model}', which is not a directory this node can see; colibri was not started. Mount the converted model there, or unset the key to drive an engine started elsewhere.",
                $"{ColibriOptions.SectionName}:Serve:{nameof(ColibriServeOptions.Model)}",
                model);
            return;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(ContentTeeVariable)))
        {
            logger.LogWarning(
                "{Variable} is set and was not passed to colibri: it tees prompts to stderr, and this node logs that stream.",
                ContentTeeVariable);
        }

        var backoff = FirstBackoff;

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = time.GetUtcNow();
            int? exitCode;

            try
            {
                exitCode = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start colibri ('{Python} {Launcher} serve').", options.Serve.Python, options.Serve.Launcher);
                exitCode = null;
            }

            if (time.GetUtcNow() - started >= StableAfter)
            {
                backoff = FirstBackoff;
            }

            logger.LogWarning("colibri exited ({ExitCode}); starting it again in {Backoff}.", exitCode?.ToString() ?? "did not start", backoff);

            try
            {
                await Task.Delay(backoff, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Kill();
    }

    public override void Dispose()
    {
        Kill();
        base.Dispose();
    }

    private async Task<int> RunOnceAsync(CancellationToken stoppingToken)
    {
        var process = Process.Start(StartInfo(options))
            ?? throw new InvalidOperationException("Process.Start returned no process.");

        child = process;

        logger.LogInformation(
            "Started colibri (pid {Pid}) serving '{ModelId}' from {Model} on 127.0.0.1:{Port} with {KvSlots} KV slot(s).",
            process.Id,
            options.Serve.ResolvedModelId(),
            options.Serve.Model,
            options.Serve.Port,
            options.KvSlots);

        // Pumped, not discarded: a model that fails to load says why on stderr and nowhere else.
        process.OutputDataReceived += (_, e) => Log(e.Data);
        process.ErrorDataReceived += (_, e) => Log(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(stoppingToken);
            return process.ExitCode;
        }
        finally
        {
            if (stoppingToken.IsCancellationRequested)
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
        // The gateway access-logs every request, including the watcher's own probe every fifteen
        // seconds; that one is not news. Everything else — loads, refusals, crashes — is.
        if (!string.IsNullOrWhiteSpace(line) && !line.Contains("\"GET /health ", StringComparison.Ordinal))
        {
            logger.LogInformation("colibri: {Line}", line);
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
                // The launcher is Python and the engine is its child; the tree is what holds the RAM.
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
