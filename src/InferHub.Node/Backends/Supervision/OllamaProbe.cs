using System.Net.Sockets;

namespace InferHub.Node.Backends.Supervision;

/// <summary>
/// <c>GET /api/version</c> over a dedicated <see cref="HttpClient"/> whose timeout is the
/// supervisor's <c>ProbeTimeout</c> (seconds), not the inference client's <c>RequestTimeout</c>
/// (minutes).
/// </summary>
/// <remarks>
/// <strong>The two clients are not redundant.</strong> Inference needs minutes because a cold
/// large model does; the probe needs seconds because a wedged Ollama that takes five minutes to
/// fail one probe takes a quarter of an hour to cross a three-probe threshold. If a future
/// reader sees two <c>HttpClient</c>s pointed at one server and consolidates them, this feature
/// silently stops working while every test still passes.
/// </remarks>
public sealed class OllamaProbe(IHttpClientFactory httpClientFactory) : IOllamaProbe
{
    public const string HttpClientName = "ollama-supervisor-probe";

    /// <summary>
    /// Set by <see cref="CreateHandler"/> once a socket is actually established, which is the
    /// only reliable way to tell <c>Unreachable</c> from <c>Wedged</c> — see the remarks there.
    /// </summary>
    private static readonly HttpRequestOptionsKey<bool> Connected = new("inferhub.probe.connected");

    /// <summary>
    /// The handler the probe's client must use, in one place so the composition root and the
    /// tests cannot drift on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The connect is done by hand so that "did the socket open?" is a fact rather than an
    /// inference from an exception.</strong> The obvious reading — connection refused throws
    /// <c>HttpRequestException</c>, a wedge throws <c>TaskCanceledException</c> — is wrong on
    /// Windows, where a closed loopback port is silently dropped rather than refused: the connect
    /// hangs to <c>ConnectTimeout</c> and surfaces as exactly the same bare
    /// <c>TaskCanceledException</c> a wedged server produces. Classifying that as a wedge would
    /// answer a server that is not running with a stop that has nothing to stop, and D3 exists
    /// precisely because that mislabels the log. Only a real socket found this; a stub handler can
    /// only echo the exception a test author already believed in.
    /// </para>
    /// <para>
    /// Pooling is off (<c>PooledConnectionLifetime = Zero</c>) so every probe connects afresh and
    /// the stamp always describes <em>this</em> probe. For one cheap request every fifteen seconds
    /// that is not a cost, and a health check riding a connection established minutes ago tells
    /// you less anyway.
    /// </para>
    /// </remarks>
    public static SocketsHttpHandler CreateHandler(TimeSpan probeTimeout)
    {
        var handler = new SocketsHttpHandler
        {
            // Connecting must fail well inside the probe's own budget, or a hung connect eats the
            // whole deadline and there is no time left to distinguish anything.
            ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(250, probeTimeout.TotalMilliseconds / 2)),
            PooledConnectionLifetime = TimeSpan.Zero
        };

        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                context.InitialRequestMessage.Options.Set(Connected, true);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        return handler;
    }

    public async Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);

        using var request = new HttpRequestMessage(HttpMethod.Get, "api/version");

        try
        {
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            // A 5xx is a server that is up and broken. Anything else that arrived promptly —
            // including a 404 from something that is not Ollama — is a responsive process, and
            // killing it over a misconfigured path would be us restarting the wrong problem.
            return (int)response.StatusCode >= 500 ? BackendHealth.Wedged : BackendHealth.Healthy;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or SocketException)
        {
            return DidConnect(request) ? BackendHealth.Wedged : BackendHealth.Unreachable;
        }
        catch (Exception)
        {
            return BackendHealth.Unreachable;
        }
    }

    private static bool DidConnect(HttpRequestMessage request)
        => request.Options.TryGetValue(Connected, out var connected) && connected;
}
