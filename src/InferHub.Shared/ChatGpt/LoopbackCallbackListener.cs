using System.Net;
using System.Net.Sockets;
using System.Text;

namespace InferHub.Shared.ChatGpt;

/// <summary>
/// The one-shot <c>http://127.0.0.1:&lt;port&gt;/auth/callback</c> listener a sign-in needs (phase 103):
/// bound to <b>127.0.0.1 only</b>, on the preferred port or the next free one, started before the
/// browser opens and closed the moment the callback arrives, is cancelled, or times out.
/// </summary>
/// <remarks>
/// A raw <see cref="TcpListener"/> rather than <c>HttpListener</c>: on Windows the latter goes
/// through http.sys, where a non-administrator cannot register an <c>http://127.0.0.1:port/</c>
/// prefix without a URL ACL — which would make "sign in" require elevation. One GET with a query
/// string is all this ever reads.
/// </remarks>
public sealed class LoopbackCallbackListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;

    private readonly TcpListener listener;

    private LoopbackCallbackListener(TcpListener listener, int port)
    {
        this.listener = listener;
        Port = port;
    }

    public int Port { get; }

    public string RedirectUri => ChatGptOAuth.RedirectUri(Port);

    /// <summary>
    /// Binds 127.0.0.1 on <paramref name="preferredPort"/>, or — when it is taken — on a free port
    /// the OS picks. Only the port ever varies; the scheme, host and path do not (the vendor's rule).
    /// </summary>
    public static LoopbackCallbackListener Start(int preferredPort = ChatGptOAuth.DefaultCallbackPort)
    {
        foreach (var port in new[] { preferredPort, 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.ExclusiveAddressUse = true;

            try
            {
                listener.Start(1);
                return new LoopbackCallbackListener(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
            }
            catch (SocketException) when (port != 0)
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException("no loopback port could be bound for the sign-in callback");
    }

    /// <summary>
    /// Waits for <c>GET /auth/callback?…</c>, answers it with a small page, and returns its query.
    /// Anything else on the port (a favicon, a probe) gets a 404 and the wait continues.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timed.CancelAfter(timeout);

        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timed.Token);
                await using var stream = client.GetStream();

                var target = await ReadRequestTargetAsync(stream, timed.Token);

                if (target is null || !target.StartsWith(ChatGptOAuth.CallbackPath, StringComparison.Ordinal)
                    || (target.Length > ChatGptOAuth.CallbackPath.Length && target[ChatGptOAuth.CallbackPath.Length] != '?'))
                {
                    await WriteAsync(stream, 404, "Not found.", timed.Token);
                    continue;
                }

                var query = ParseQuery(target.Contains('?') ? target[(target.IndexOf('?') + 1)..] : string.Empty);

                var page = query.ContainsKey("error")
                    ? "The sign-in was not completed. You can close this tab and return to InferHub."
                    : "Signed in. You can close this tab and return to InferHub.";

                await WriteAsync(stream, 200, page, timed.Token);
                return query;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatGptOAuthException("timeout",
                $"no browser came back to {RedirectUri} within {timeout.TotalMinutes:0} minutes; the sign-in was abandoned.");
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose() => listener.Stop();

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            result.TryAdd(key, value);
        }

        return result;
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var read = 0;

        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);

            if (n == 0)
            {
                break;
            }

            read += n;

            if (buffer.AsSpan(0, read).IndexOf("\r\n\r\n"u8) >= 0)
            {
                break;
            }
        }

        var head = Encoding.ASCII.GetString(buffer, 0, read);
        var firstLine = head.Split("\r\n", 2)[0].Split(' ');

        return firstLine.Length >= 2 && firstLine[0] == "GET" ? firstLine[1] : null;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string text, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>InferHub</title>"
            + $"<body style=\"font-family:system-ui;margin:3rem\"><p>{WebUtility.HtmlEncode(text)}</p></body>");

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + "Cache-Control: no-store\r\n"
            + "Referrer-Policy: no-referrer\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
    }
}
