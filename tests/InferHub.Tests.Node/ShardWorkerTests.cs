using InferHub.Node.Backends;
using InferHub.Node.Backends.Shards;
using InferHub.Node.Configuration;
using InferHub.Shared.Fit;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// Phase 134 on the node: the worker is off unless the owner turned it on (D1), finds llama.cpp's own worker beside the
/// node's llama-server, binds loopback whatever is configured, refuses with a sentence rather than a process (D2), and
/// is "running" when it has announced its protocol and devices — not when it was launched.
/// </summary>
public sealed class ShardWorkerTests
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? ".exe" : "";

    private static string Llama => Path.Combine(Path.GetTempPath(), "llama");

    private static BackendOptions WithLlama(string executable) => new()
    {
        Engines = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gguf"] = new EngineOptions { Type = "llamacpp", Serve = new EngineServeOptions { Executable = executable } }
        }
    };

    private static ShardWorker Worker(ShardOptions options, BackendOptions? backend = null, Func<string, bool>? exists = null) =>
        new(options, backend ?? WithLlama(Path.Combine(Llama, "llama-server" + Exe)), NullLogger.Instance, null,
            exists ?? (path => path == Path.Combine(Llama, "ggml-rpc-server" + Exe)), null);

    [Fact]
    public void OffIsNothingOfferedNothingLaunchedAndAnOffState()
    {
        var worker = Worker(new ShardOptions { RamGiB = 24 });

        Assert.False(worker.Enabled);
        Assert.Null(worker.Offer());
        Assert.Equal(NodeShardState.Off, worker.State("n1").State);
        Assert.Equal(0, worker.State("n1").RamMiB);
    }

    [Fact]
    public void TheWorkerBesideTheNodesLlamaServerIsFoundAndAnExplicitOneWins()
    {
        var beside = Path.Combine(Llama, "ggml-rpc-server" + Exe);

        Assert.Equal(beside, ShardWorker.Locate(new ShardOptions(), WithLlama(Path.Combine(Llama, "llama-server" + Exe)), p => p == beside, null));
        Assert.Equal("/opt/rpc", ShardWorker.Locate(new ShardOptions { Executable = "/opt/rpc" }, WithLlama("llama-server"), _ => false, null));
        Assert.Null(ShardWorker.Locate(new ShardOptions(), new BackendOptions(), _ => true, null));

        // A bare llama-server: the directory on PATH where it resolves.
        var onPath = Path.Combine(Path.GetTempPath(), "bin");
        var found = ShardWorker.Locate(new ShardOptions(), WithLlama("llama-server"),
            p => p == Path.Combine(onPath, "llama-server" + Exe) || p == Path.Combine(onPath, "rpc-server" + Exe),
            string.Join(Path.PathSeparator, Path.Combine(Path.GetTempPath(), "elsewhere"), onPath));
        Assert.Equal(Path.Combine(onPath, "rpc-server" + Exe), found);
    }

    [Fact]
    public void AWorkerThatCannotRunIsOneSentenceAndNoProcess()
    {
        Assert.Contains("offers nothing", ShardWorker.Refusal(new ShardOptions { Enabled = true }, "/x/rpc"));
        Assert.Contains("no llama.cpp RPC worker found", ShardWorker.Refusal(new ShardOptions { Enabled = true, RamGiB = 8 }, null));
        Assert.Contains("does not exist", ShardWorker.Refusal(new ShardOptions { Enabled = true, RamGiB = 8 }, "/x/rpc", _ => false));
        Assert.Contains("Port", ShardWorker.Refusal(new ShardOptions { Enabled = true, RamGiB = 8, Port = 0 }, "/x/rpc", _ => true));
        Assert.Null(ShardWorker.Refusal(new ShardOptions { Enabled = true, VramGiB = 8 }, "/x/rpc", _ => true));

        var worker = Worker(new ShardOptions { Enabled = true, RamGiB = 8 }, new BackendOptions(), _ => false);
        var state = worker.State("n1");
        Assert.Equal(NodeShardState.Failed, state.State);
        Assert.Contains("no llama.cpp RPC worker found", state.LastError);
    }

    [Fact]
    public void TheWorkerListensOnLoopbackWhateverElseIsSet()
    {
        Assert.Equal(["-H", "127.0.0.1", "-p", "50052", "-c"], ShardWorker.Arguments(new ShardOptions { Enabled = true, RamGiB = 8 }));
        Assert.Equal(["-H", "127.0.0.1", "-p", "6000", "-t", "4", "-d", "Vulkan0,CPU"],
            ShardWorker.Arguments(new ShardOptions { Port = 6000, Threads = 4, Devices = "Vulkan0,CPU", Cache = false }));

        // D1: there is no host key to set — the type itself has none.
        Assert.Null(typeof(ShardOptions).GetProperty("Host"));
    }

    [Fact]
    public void TheBannerSaysWhatItOffersButNotThatItListens()
    {
        var worker = Worker(new ShardOptions { Enabled = true, RamGiB = 24, VramGiB = 4 });
        var changes = 0;
        worker.Changed += () => changes++;

        Assert.NotEqual(NodeShardState.Running, worker.State("n1").State);
        Assert.Equal(new ShardOffer(24 * 1024, 4 * 1024), worker.Offer());

        foreach (var line in new[]
                 {
                     "load_backend: loaded RPC backend from /app/libggml-rpc.so",
                     "Starting RPC server v8.0.1",
                     "  endpoint       : 127.0.0.1:50052",
                     "  local cache    : /root/.cache/llama.cpp/rpc/",
                     "Devices:",
                     "  CPU: AMD Ryzen Threadripper PRO 5975WX 32-Cores (128724 MiB, 128724 MiB free)",
                     "  transport      : TCP"
                 })
        {
            worker.Read(line);
        }

        // Printed before the socket — and on Windows held in the pipe until exit — so it is never "running" by itself.
        var state = worker.State("n1");
        Assert.NotEqual(NodeShardState.Running, state.State);
        Assert.Equal("8.0.1", state.Protocol);
        Assert.Equal("127.0.0.1:50052", state.Endpoint);
        Assert.Equal(["CPU: AMD Ryzen Threadripper PRO 5975WX 32-Cores (128724 MiB, 128724 MiB free)"], state.Devices);
        Assert.Equal("8.0.1", worker.Offer()!.Protocol);
        Assert.True(changes >= 1);
    }

    [Fact]
    public void AWorkerThatCouldNotListenSaysItInItsOwnWords()
    {
        var worker = Worker(new ShardOptions { Enabled = true, RamGiB = 8, Port = 50199 });

        // Found live on Windows: a port in a range the system reserved. stderr first, the banner flushed at exit.
        worker.Read("Failed to create server socket");
        worker.Read("Starting RPC server v5.0.0");
        worker.Read("  CPU: AMD Ryzen Threadripper PRO 5975WX 32-Cores      (261985 MiB, 113592 MiB free)");
        worker.Exited();

        var state = worker.State("n1");
        Assert.Equal(NodeShardState.Failed, state.State);
        Assert.Contains("Failed to create server socket", state.LastError);
    }

    [Fact]
    public async Task TheHandshakeIsTheOneAHeadSendsAndTheAnswerIsTheProtocol()
    {
        // A worker as llama.cpp's source writes it: HELLO is command 14, 24 bytes of capabilities; the answer is its
        // size (28), then major, minor, patch, padding and 24 bytes.
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        byte[]? asked = null;

        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            asked = new byte[33];
            await stream.ReadExactlyAsync(asked);
            var answer = new byte[36];
            BitConverter.TryWriteBytes(answer.AsSpan(0, 8), 28UL);
            answer[8] = 8;
            answer[9] = 0;
            answer[10] = 1;
            await stream.WriteAsync(answer);
        });

        Assert.Equal("8.0.1", await ShardWorker.HelloAsync(port, TimeSpan.FromSeconds(5), CancellationToken.None));
        await serve;
        Assert.Equal(14, asked![0]);
        Assert.Equal(24UL, BitConverter.ToUInt64(asked, 1));

        // Nothing listening: no protocol, no exception.
        listener.Stop();
        Assert.Null(await ShardWorker.HelloAsync(port, TimeSpan.FromSeconds(2), CancellationToken.None));
    }
}
