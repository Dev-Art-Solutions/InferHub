using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// One live child process, and the only place in the node that starts one for a tool
/// (phase 41, D1/D3).
/// </summary>
/// <remarks>
/// <para>
/// The node knows how to start a process, write a line, read a line and kill it. It knows nothing
/// about Python. That is the whole of D1: the alternative — an in-process binding — pins the node
/// to a CPython ABI, turns one bad <c>import</c> into a segfault that takes every in-flight
/// inference job with it, and forecloses the tool that is a Go binary or a vendor's CLI. A child
/// process that segfaults is a log line and a restart.
/// </para>
/// <para>
/// A worker serves <b>one request at a time</b>. The pool is what provides concurrency, and it
/// hands out exclusive leases, so nothing here needs to multiplex a correlation id across
/// overlapping requests — the id exists so a worker author cannot get it wrong, not because the
/// node interleaves.
/// </para>
/// </remarks>
internal sealed class ToolWorkerProcess : IAsyncDisposable
{
    /// <summary>
    /// What a worker inherits from the node's environment, and nothing else (D3).
    /// </summary>
    /// <remarks>
    /// The node's environment holds <c>Auth__NodeEnrollmentSecret</c>, <c>LocalApi__ApiKeys__0</c>
    /// and whatever else the deployment set. Handing all of it to a third-party script is a
    /// credential leak wearing a convenience's clothes — and unlike most leaks it is invisible,
    /// because the script never has to do anything with it for the exposure to be real. An operator
    /// who genuinely needs a variable names it in the manifest's <c>env</c>.
    /// </remarks>
    private static readonly string[] PassThrough =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "TMPDIR", "USER", "SHELL"
    ];

    /// <summary>
    /// Windows cannot start a process without these, so dropping them would mean the runtime is
    /// unusable on the platform its own test suite runs on. They carry no secrets.
    /// </summary>
    private static readonly string[] WindowsEssentials =
    [
        "SystemRoot", "SystemDrive", "windir", "COMSPEC", "PATHEXT", "TEMP", "TMP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "USERPROFILE", "APPDATA", "LOCALAPPDATA"
    ];

    private readonly ToolManifest manifest;
    private readonly ILogger logger;
    private readonly Process process;
    private readonly StreamWriter input;
    private readonly StreamReader output;
    private readonly Task stderrPump;
    private readonly CancellationTokenSource lifetime = new();

    private ToolWorkerProcess(ToolManifest manifest, Process process, ILogger logger)
    {
        this.manifest = manifest;
        this.process = process;
        this.logger = logger;

        input = process.StandardInput;
        output = process.StandardOutput;
        stderrPump = Task.Run(PumpStandardErrorAsync);
        LastUsed = DateTimeOffset.UtcNow;
    }

    public DateTimeOffset LastUsed { get; private set; }

    /// <summary>What the worker reported at handshake, or null when it reported nothing.</summary>
    public IReadOnlyList<NodeCapability>? ReportedCapabilities { get; private set; }

    /// <summary>
    /// Total VRAM the worker could see, in MiB, or null when it did not say (phase 48, D1). A
    /// <b>cross-check</b> for the node to log against the declared budget — never an override.
    /// </summary>
    public int? ReportedVramTotalMiB { get; private set; }

    /// <summary>
    /// Whether this worker has already been told it is idle in the current idle period. Cleared the
    /// moment it serves anything, so a worker that goes quiet again is hinted again — and hinted
    /// <em>once</em> rather than every maintenance tick, which would be a frame every 30 seconds
    /// forever on a box nobody is using.
    /// </summary>
    public bool IdleHinted { get; private set; }

    /// <summary>
    /// Raised when a worker sends a <c>ready</c> frame <em>after</em> the handshake (v3.14.1), so
    /// the pool can re-narrow and the node can re-report to its coordinator without a restart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The case that forced it: a diffusion worker whose weights are still downloading declares
    /// only the recipes it can actually serve, so the fleet never routes at a model that is not
    /// ready. Without a way to say "and now this one too", the model would stay undeclared until
    /// the worker was restarted — and nothing would restart it, because nothing would route to it.
    /// </para>
    /// <para>
    /// The narrowing rule is unchanged: this only ever hands the pool a <em>reported</em> set,
    /// which <c>ToolWorkerPool.Narrow</c> still clamps to the manifest. A worker cannot widen its
    /// own grant by re-declaring, any more than it could at handshake.
    /// </para>
    /// </remarks>
    public event Action? CapabilitiesRedeclared;

    private void ApplyRedeclaration(ToolFrame frame)
    {
        if (frame.Capabilities is null)
        {
            return;
        }

        ReportedCapabilities = frame.Capabilities;

        logger.LogInformation(
            "Tool '{ToolId}' re-declared its capabilities: {Capabilities}",
            manifest.Id,
            string.Join(", ", frame.Capabilities.Select(c => $"{c.Kind}[{string.Join(' ', c.Models)}]")));

        CapabilitiesRedeclared?.Invoke();
    }

    public bool IsAlive
    {
        get
        {
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Spawns the worker and completes its handshake, or throws. A failure here is a failure to
    /// start — the caller counts it against the restart budget.
    /// </summary>
    public static async Task<ToolWorkerProcess> StartAsync(
        ToolManifest manifest,
        IReadOnlyDictionary<string, string> nodeEnvironment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = manifest.Command[0],
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        // ArgumentList, never Arguments: the framework quotes each element for the platform, so
        // there is no string for a space or a quote to escape out of.
        foreach (var argument in manifest.Command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(manifest.WorkingDirectory))
        {
            startInfo.WorkingDirectory = manifest.WorkingDirectory;
        }

        ApplyEnvironment(startInfo, manifest, nodeEnvironment);

        var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            process.Dispose();

            throw new ToolStartException(
                $"could not start tool '{manifest.Id}' ({manifest.Command[0]}): {ex.Message}",
                ex);
        }

        var worker = new ToolWorkerProcess(manifest, process, logger);

        try
        {
            await worker.HandshakeAsync(cancellationToken);
            return worker;
        }
        catch
        {
            await worker.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Sends a request and yields every frame the worker produces for it, ending with the result or
    /// the error. Overrunning <c>requestTimeoutSeconds</c>, or the worker dying, both surface here
    /// as an exception the pool turns into a failed job.
    /// </summary>
    public async IAsyncEnumerable<ToolFrame> ExecuteAsync(
        ToolFrame request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(manifest.RequestTimeout);

        await SendAsync(request, deadline.Token);

        while (true)
        {
            ToolFrame? frame;

            try
            {
                frame = await ReadFrameAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolRequestTimeoutException(
                    $"tool '{manifest.Id}' did not answer within {manifest.RequestTimeoutSeconds}s");
            }

            if (frame is null)
            {
                throw new ToolWorkerDiedException(
                    $"tool '{manifest.Id}' stopped answering before this request finished{TryDescribeExit()}");
            }

            switch (frame.Type)
            {
                case ToolFrameTypes.Chunk:
                case ToolFrameTypes.Progress:
                    yield return frame;
                    continue;

                case ToolFrameTypes.Result:
                case ToolFrameTypes.Error:
                    LastUsed = DateTimeOffset.UtcNow;
                    IdleHinted = false;
                    yield return frame;
                    yield break;

                case ToolFrameTypes.Log:
                    LogWorkerLine(frame.Level, frame.Message);
                    continue;

                case ToolFrameTypes.Ready:
                    // A LATE READY (v3.14.1): the worker's answer to "what can you do" changed
                    // while it was running. Until v3.14.1 this branch discarded it, which made a
                    // worker that becomes able to do more — weights that finished downloading, a
                    // voice dropped onto the volume — unable to say so without being restarted.
                    ApplyRedeclaration(frame);
                    continue;

                default:
                    // pong, anything a future worker invents: not an answer to this request, and
                    // not a reason to fail it.
                    continue;
            }
        }
    }

    /// <summary>
    /// Asks the worker to stop working on a request, cooperatively (phase 47, D3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It writes one frame and returns; it does not wait, and it deliberately does not touch the
    /// read loop. The worker's answer — an <c>error</c> coded <c>cancelled</c>, or the result it
    /// finished anyway — arrives on the same enumeration the request is already being read from, so
    /// there is exactly one reader of this process's stdout at any moment.
    /// </para>
    /// <para>
    /// False means the frame could not be written: the worker is gone, or its stdin is closed. The
    /// caller falls straight through to the grace deadline rather than treating it as a failure of
    /// its own, because a worker that cannot be asked will not be answering either.
    /// </para>
    /// </remarks>
    public async Task<bool> CancelAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            return false;
        }

        try
        {
            await SendAsync(new ToolFrame { Type = ToolFrameTypes.Cancel, Id = requestId }, cancellationToken);

            logger.LogInformation(
                "Asked tool '{ToolId}' to cancel request {RequestId}; it has the cancel grace to answer.",
                manifest.Id,
                requestId);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not send a cancel frame to tool '{ToolId}'", manifest.Id);
            return false;
        }
    }

    /// <summary>
    /// Tells the worker nobody has asked it for anything in <c>idleTimeoutSeconds</c> (phase 48, D3).
    /// </summary>
    /// <remarks>
    /// <b>A hint, not an instruction, and there is no reply to wait for.</b> The node does not know
    /// what this worker is holding or how to free it — that would be the node reaching into a tool's
    /// internals, which is phase-41 D1's line. A diffusion worker frees its pipelines and stays
    /// alive; anything else ignores the frame. Failing to send it is a debug line: an idle worker
    /// holding memory is a waste, not a fault.
    /// </remarks>
    public async Task<bool> HintIdleAsync(CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            return false;
        }

        try
        {
            await SendAsync(new ToolFrame { Type = ToolFrameTypes.Idle }, cancellationToken);
            IdleHinted = true;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not send an idle hint to tool '{ToolId}'", manifest.Id);
            return false;
        }
    }

    /// <summary>Liveness probe for an idle worker. False means it is gone or wedged.</summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IsAlive)
        {
            return false;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);

        try
        {
            await SendAsync(new ToolFrame { Type = ToolFrameTypes.Ping }, deadline.Token);

            while (await ReadFrameAsync(deadline.Token) is { } frame)
            {
                if (frame.Type is ToolFrameTypes.Pong)
                {
                    return true;
                }

                if (frame.Type is ToolFrameTypes.Log)
                {
                    LogWorkerLine(frame.Level, frame.Message);
                }

                // An idle worker has nobody reading its stdout, so a late `ready` sits in the pipe
                // until something drains it. This probe is that something — which is the second
                // reason the maintenance loop pings (the first being the liveness check phase-41 D6
                // specified and v3.14.0 never wired up).
                if (frame.Type is ToolFrameTypes.Ready)
                {
                    ApplyRedeclaration(frame);
                }
            }

            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.FromSeconds(5));

    /// <summary>
    /// Stop a worker that has already failed: close stdin and kill, with no grace period.
    /// </summary>
    /// <remarks>
    /// The grace exists so a <em>cooperative</em> worker can close a half-written file. One that
    /// blew its request deadline or died mid-answer is by definition not cooperating, and waiting
    /// five seconds for it to be polite holds the pool's only slot against exactly the requests
    /// that have already waited longest.
    /// </remarks>
    public ValueTask TerminateAsync() => StopAsync(TimeSpan.Zero);

    private async ValueTask StopAsync(TimeSpan grace)
    {
        await lifetime.CancelAsync();

        // Closing stdin is the graceful stop: a worker's read loop ends, its `finally` runs, and a
        // half-written model file gets closed. Only then is it killed.
        try
        {
            input.Close();
        }
        catch (Exception)
        {
        }

        try
        {
            if (!process.HasExited)
            {
                if (grace > TimeSpan.Zero)
                {
                    using var deadline = new CancellationTokenSource(grace);

                    try
                    {
                        await process.WaitForExitAsync(deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning(
                            "Tool '{ToolId}' did not exit within {Grace} of its stdin closing; killing it.",
                            manifest.Id,
                            grace);

                        process.Kill(entireProcessTree: true);
                    }
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not stop tool '{ToolId}' cleanly", manifest.Id);
        }

        try
        {
            await stderrPump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        process.Dispose();
        lifetime.Dispose();
    }

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        ToolManifest manifest,
        IReadOnlyDictionary<string, string> nodeEnvironment)
    {
        // ProcessStartInfo pre-populates Environment from *this* process. Clearing it is the whole
        // of the leak fix, and it has to happen before anything is added back.
        startInfo.Environment.Clear();

        var inherited = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? PassThrough.Concat(WindowsEssentials)
            : PassThrough;

        foreach (var name in inherited)
        {
            if (System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                startInfo.Environment[name] = value;
            }
        }

        // The node's own consent flags, added before the manifest so a manifest can still override
        // one deliberately. They are not inherited — nothing is (see the Clear above); they are
        // *stated*, which is why a tool cannot acquire one by being started in the right shell.
        foreach (var (name, value) in nodeEnvironment)
        {
            startInfo.Environment[name] = value;
        }

        foreach (var (name, value) in manifest.Environment)
        {
            startInfo.Environment[name] = value;
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(manifest.StartTimeout);

        await SendAsync(
            new ToolFrame
            {
                Type = ToolFrameTypes.Hello,
                Protocol = ToolProtocol.Version,
                Tool = manifest.Id
            },
            deadline.Token);

        while (true)
        {
            ToolFrame? frame;

            try
            {
                frame = await ReadFrameAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ToolStartException(
                    $"tool '{manifest.Id}' did not send a 'ready' frame within {manifest.StartTimeoutSeconds}s");
            }

            if (frame is null)
            {
                throw new ToolStartException(
                    $"tool '{manifest.Id}' exited during startup{TryDescribeExit()}. Its stderr is in this log under the tool's id.");
            }

            if (frame.Type is ToolFrameTypes.Log)
            {
                LogWorkerLine(frame.Level, frame.Message);
                continue;
            }

            if (frame.Type is not ToolFrameTypes.Ready)
            {
                continue;
            }

            if (frame.Protocol is { } protocol && protocol != ToolProtocol.Version)
            {
                throw new ToolStartException(
                    $"tool '{manifest.Id}' speaks protocol version {protocol}; this node speaks {ToolProtocol.Version}.");
            }

            ReportedCapabilities = frame.Capabilities;
            ReportedVramTotalMiB = frame.VramTotalMiB;
            LastUsed = DateTimeOffset.UtcNow;
            return;
        }
    }

    private async Task SendAsync(ToolFrame frame, CancellationToken cancellationToken)
    {
        await input.WriteLineAsync(ToolProtocol.Write(frame).AsMemory(), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    private async Task<ToolFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await output.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                return null;
            }

            if (ToolProtocol.TryRead(line) is { } frame)
            {
                return frame;
            }

            // Not a frame. A library's progress bar on stdout must not take the worker down; it is
            // the tool author's mistake and the log is where they will find it.
            logger.LogDebug("Tool '{ToolId}' wrote a non-frame line to stdout: {Line}", manifest.Id, line);
        }
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 0)
                {
                    // stderr is NOT protocol (D5). It is where a Python traceback goes, and a
                    // traceback is the single most useful thing a tool author ever sees — so it is
                    // relayed verbatim, scoped by the tool's id, rather than being parsed.
                    logger.LogInformation("[tool {ToolId}] {Line}", manifest.Id, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "stderr pump for tool '{ToolId}' ended", manifest.Id);
        }
    }

    private void LogWorkerLine(string? level, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var logLevel = level?.ToLowerInvariant() switch
        {
            "debug" or "trace" => LogLevel.Debug,
            "warn" or "warning" => LogLevel.Warning,
            "error" or "fatal" or "critical" => LogLevel.Error,
            _ => LogLevel.Information
        };

        logger.Log(logLevel, "[tool {ToolId}] {Message}", manifest.Id, message);
    }

    /// <summary>
    /// The exit code, when there is one to report.
    /// </summary>
    /// <remarks>
    /// A worker killed with SIGKILL closes its stdout before the OS has reaped it, so this path is
    /// reached with <c>HasExited</c> still false — and the first version of the sentence read
    /// "exited before answering (exit code still running)", which is a contradiction the reader has
    /// to decode. Reporting nothing is better than reporting a non-answer as if it were one.
    /// </remarks>
    private string TryDescribeExit()
    {
        try
        {
            return process.HasExited ? $" (exit code {process.ExitCode})" : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}

/// <summary>A worker could not be started, or never finished its handshake.</summary>
internal sealed class ToolStartException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>A worker did not answer within the manifest's request deadline.</summary>
internal sealed class ToolRequestTimeoutException(string message) : InvalidOperationException(message);

/// <summary>A worker exited while a request was in flight.</summary>
internal sealed class ToolWorkerDiedException(string message) : InvalidOperationException(message);
