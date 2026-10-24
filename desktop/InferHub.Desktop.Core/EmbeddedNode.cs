using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace InferHub.Desktop;

/// <summary>
/// The app's own node (phase 115 D2): the node the release carries, run as a child with its local API on loopback and
/// its data and <c>node.local.json</c> in the user's app-data directory — so a person with nothing installed has a
/// node, and every setting the app writes stays theirs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit code 75 is a restart</b> (112 D3): the node asked to be started again (<c>INFERHUB_SUPERVISOR=desktop</c>
/// told it the app would). Any other exit is reported and left stopped; a node that fails at boot says why in its own
/// log, which the app shows.
/// </para>
/// <para>
/// <b>The environment is a default, never a lock.</b> Values the app passes (local API on, no coordinator) are
/// environment variables, and the node reads <c>node.local.json</c> after them — so "join a network" in the app's
/// settings is a value in that file that wins, not a fight with the launcher.
/// </para>
/// </remarks>
public sealed class EmbeddedNode : IDisposable
{
    public const int RestartExitCode = 75;

    private readonly object gate = new();
    private Process? process;
    private bool stopping;
    private StreamWriter? console;

    public EmbeddedNode(string? executable = null, string? dataDirectory = null, int? port = null)
    {
        Executable = executable ?? FindExecutable();
        DataDirectory = dataDirectory ?? AppPaths.NodeData;
        Port = port ?? PickPort();
    }

    /// <summary>Raised when the node starts, restarts or stops, with a sentence.</summary>
    public event Action<string>? StateChanged;

    public string? Executable { get; }

    public string DataDirectory { get; }

    public int Port { get; }

    public Uri Url => new($"http://127.0.0.1:{Port}");

    public string TokenFile => Path.Combine(DataDirectory, "control.token");

    public string ConsoleLog => Path.Combine(DataDirectory, "node-console.log");

    public bool Available => Executable is not null && File.Exists(Executable);

    public bool Running
    {
        get
        {
            lock (gate)
            {
                return process is { HasExited: false };
            }
        }
    }

    /// <summary>The node beside the app: <c>node/InferHub.Node[.exe]</c>; <c>INFERHUB_DESKTOP_NODE</c> overrides it (development).</summary>
    public static string? FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("INFERHUB_DESKTOP_NODE") is { Length: > 0 } custom)
        {
            return custom;
        }

        var name = OperatingSystem.IsWindows() ? "InferHub.Node.exe" : "InferHub.Node";
        var path = Path.Combine(AppContext.BaseDirectory, "node", name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The command line and environment the node starts with — pure, so it is tested without a process.</summary>
    public ProcessStartInfo StartInfo()
    {
        var info = new ProcessStartInfo(Executable ?? throw new InvalidOperationException("this app carries no node"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Executable)!
        };

        info.Environment["LocalApi__Enabled"] = "true";
        info.Environment["LocalApi__Urls"] = Url.ToString().TrimEnd('/');
        info.Environment["Coordinator__Enabled"] = "false";
        info.Environment["Node__DataDirectory"] = DataDirectory;
        info.Environment["Node__LocalSettingsFile"] = Path.Combine(DataDirectory, "node.local.json");
        info.Environment["INFERHUB_SUPERVISOR"] = "desktop";
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        return info;
    }

    public void Start()
    {
        lock (gate)
        {
            if (process is { HasExited: false })
            {
                return;
            }

            stopping = false;
            Directory.CreateDirectory(DataDirectory);
            console ??= new StreamWriter(new FileStream(ConsoleLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

            var started = Process.Start(StartInfo()) ?? throw new InvalidOperationException("the node did not start");
            started.EnableRaisingEvents = true;
            started.OutputDataReceived += (_, e) => Write(e.Data);
            started.ErrorDataReceived += (_, e) => Write(e.Data);
            started.Exited += (_, _) => OnExited(started);
            started.BeginOutputReadLine();
            started.BeginErrorReadLine();
            process = started;
        }

        StateChanged?.Invoke($"started this app's node on {Url} (pid {process!.Id})");
    }

    public void Stop()
    {
        Process? running;

        lock (gate)
        {
            stopping = true;
            running = process;
        }

        if (running is { HasExited: false })
        {
            try
            {
                running.Kill(entireProcessTree: true);
                running.WaitForExit(10_000);
            }
            catch (InvalidOperationException)
            {
            }
        }

        StateChanged?.Invoke("this app's node is stopped");
    }

    private void OnExited(Process exited)
    {
        int code;

        try
        {
            code = exited.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        bool restart;

        lock (gate)
        {
            restart = !stopping && code == RestartExitCode && ReferenceEquals(process, exited);
        }

        if (restart)
        {
            StateChanged?.Invoke("the node asked to be restarted; starting it again");
            Start();
        }
        else if (!stopping)
        {
            StateChanged?.Invoke($"this app's node stopped (exit code {code}); its log is {ConsoleLog}");
        }
    }

    private void Write(string? line)
    {
        if (line is not null)
        {
            lock (gate)
            {
                console?.WriteLine(line);
            }
        }
    }

    /// <summary>5081, the node's own default, when it is free; another free loopback port otherwise.</summary>
    internal static int PickPort(int preferred = 5081)
    {
        if (Free(preferred))
        {
            return preferred;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool Free(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        console?.Dispose();
    }
}
