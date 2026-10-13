using InferHub.Shared.ChatGpt;

// inferhub-signin --hub <https://coordinator> [--code <pairing code>] [--port 1234] [--no-browser] [--allow-http]
//
// The coordinator's console creates the pairing code (Cloud providers → ChatGPT → Continue with ChatGPT).
// The code is read from stdin when --code is absent, so it need not sit in this shell's history.
Console.OutputEncoding = System.Text.Encoding.UTF8;

string? hub = null, code = null;
var port = ChatGptOAuth.DefaultCallbackPort;
var openBrowser = true;
var allowHttp = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--hub" when i + 1 < args.Length:
            hub = args[++i];
            break;
        case "--code" when i + 1 < args.Length:
            code = args[++i];
            break;
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var p):
            port = p;
            i++;
            break;
        case "--no-browser":
            openBrowser = false;
            break;
        case "--allow-http":
            allowHttp = true;
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"unknown argument '{args[i]}'");
            PrintUsage();
            return 1;
    }
}

if (hub is null || !Uri.TryCreate(hub, UriKind.Absolute, out var hubUri))
{
    PrintUsage();
    return 1;
}

if (string.IsNullOrWhiteSpace(code))
{
    Console.Write("Pairing code from the coordinator's console: ");
    code = Console.ReadLine();
}

if (string.IsNullOrWhiteSpace(code))
{
    Console.Error.WriteLine("No pairing code was given.");
    return 1;
}

try
{
    var result = await ChatGptPairing.RunAsync(hubUri, code, port, allowHttp, openBrowser, Console.Out, CancellationToken.None);
    Console.WriteLine(result.Message);
    return result.PlanUsage ? 0 : 2;
}
catch (Exception ex) when (ex is ChatGptOAuthException or HttpRequestException or InvalidOperationException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static void PrintUsage() => Console.WriteLine("""
    inferhub-signin --hub <https://coordinator> [--code <pairing code>] [--port 1234] [--no-browser] [--allow-http]

    Signs a remote InferHub coordinator in to ChatGPT. Run it on the computer whose browser you use;
    get the pairing code from the coordinator's console (ChatGPT → Continue with ChatGPT).
    """);
