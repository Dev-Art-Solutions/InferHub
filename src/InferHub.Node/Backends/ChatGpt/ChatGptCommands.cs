using InferHub.Shared.ChatGpt;

namespace InferHub.Node.Backends.ChatGpt;

/// <summary>
/// <c>chatgpt login | status | logout | use | pair</c> — the node's companion sign-in (phase 103). Runs
/// and exits before any host exists, beside a running node or service, and writes the same store the
/// node reads; the node notices the new write on its next request.
/// </summary>
/// <remarks>
/// <para>
/// <b>A Windows service never opens a browser</b> — session 0 has no desktop. This verb is the
/// interactive half: run it as the user at the node's console, and it opens that user's browser on a
/// 127.0.0.1 callback. An administrator's shell is needed only where the store's directory is
/// administrator-only, which is where the setup puts a service's data.
/// </para>
/// <para>
/// <c>pair</c> is the other direction: the sign-in helper for a <b>coordinator</b>, given its address
/// and a pairing code from its console. It writes nothing on this machine. It lives here too because
/// a Windows node's setup is the one InferHub binary an administrator is likely to have installed.
/// </para>
/// </remarks>
public static class ChatGptCommands
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "chatgpt";

    /// <param name="args">The whole command line, starting at <c>chatgpt</c>.</param>
    /// <param name="contentRoot">Where this host resolves a relative <c>Node:DataDirectory</c> — the working
    /// directory for the console host, the executable's directory for the service host.</param>
    /// <param name="settingsFile">The Windows setup's settings file, when this is the service host.</param>
    public static async Task<int> RunAsync(string[] args, string contentRoot, string? settingsFile, TextWriter output, TextWriter error)
    {
        var verb = args.Length > 1 ? args[1] : "help";
        var options = Parse(args.Skip(2).ToArray());

        try
        {
            if (verb == "pair")
            {
                return await PairAsync(options, output);
            }

            var configuration = NodeHostFactory.ReadConfiguration([], settingsFile);
            var node = configuration.GetSection(ChatGptNodeOptions.SectionName).Get<ChatGptNodeOptions>() ?? new ChatGptNodeOptions();
            var directory = options.TryGetValue("--data-dir", out var dir) && dir is not null
                ? Path.GetFullPath(dir)
                : ChatGptNodeOptions.ResolveDirectory(configuration, contentRoot);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var session = new ChatGptSession(new ChatGptCredentialStore(directory), () => new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, IdTokenValidator.Over(http));

            switch (verb)
            {
                case "login":
                    return await LoginAsync(session, node, options, http, output);

                case "status":
                    Print(session, directory, output);
                    return 0;

                case "logout":
                    var revoked = await session.SignOutAsync(Value(options, "--account"), CancellationToken.None);
                    output.WriteLine(revoked
                        ? "Signed out: the session was revoked at OpenAI and the tokens were removed from this node."
                        : "Signed out locally. OpenAI did not confirm the revocation — disconnect the app in ChatGPT Settings to be sure.");
                    return 0;

                case "use" when Value(options, "--account") is { } account:
                    await session.ActivateAsync(account, CancellationToken.None);
                    output.WriteLine($"This node now uses ChatGPT account {account}.");
                    return 0;

                default:
                    output.WriteLine(Usage);
                    return verb is "help" or "--help" ? 0 : 1;
            }
        }
        catch (Exception ex) when (ex is ChatGptOAuthException or ChatGptStoreException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public const string Usage = """
        chatgpt login  [--port 1234] [--new-account] [--account <id>] [--no-browser] [--data-dir <dir>]
                       Sign this node in to its own ChatGPT account in this computer's browser.
        chatgpt status [--data-dir <dir>]
        chatgpt logout [--account <id>] [--data-dir <dir>]
        chatgpt use    --account <id> [--data-dir <dir>]
        chatgpt pair   --hub <https://coordinator> [--code <pairing code>] [--port 1234] [--no-browser] [--allow-http]
                       Sign a COORDINATOR in, with a pairing code from its console. Writes nothing here.
        """;

    private static async Task<int> LoginAsync(ChatGptSession session, ChatGptNodeOptions node, Dictionary<string, string?> options, HttpClient http, TextWriter output)
    {
        var newAccount = options.ContainsKey("--new-account");
        var hints = session.Hints(Value(options, "--account"), newAccount);
        var port = int.TryParse(Value(options, "--port"), out var p) ? p : node.CallbackPort;

        var signIn = ChatGptBrowserSignIn.Begin(session.HostId(), hints, port);

        output.WriteLine(hints.ClientId is null
            ? "Registering this node with a ChatGPT account."
            : $"Reconnecting {hints.LoginHint ?? hints.RegistrationId}.");
        output.WriteLine($"Waiting for the browser on {signIn.Attempt.RedirectUri} …");

        if (options.ContainsKey("--no-browser") || !ChatGptBrowserSignIn.TryOpenBrowser(signIn.AuthorizeUrl))
        {
            output.WriteLine("Open this address in a browser on THIS computer:");
            output.WriteLine(signIn.AuthorizeUrl);
        }

        var grant = await signIn.CompleteAsync(http, session.Validator, hints.RegistrationId, null, CancellationToken.None);
        var state = await session.ApplySignInAsync(grant, CancellationToken.None);

        output.WriteLine($"Signed in as {state.Email ?? state.Account}.");
        output.WriteLine(state.PlanUsage
            ? "ChatGPT plan usage is granted. The node picks this up on its next request — no restart."
            : "ChatGPT plan usage was NOT granted, so this node will refuse inference. Run 'chatgpt login' again and allow it.");

        return 0;
    }

    private static async Task<int> PairAsync(Dictionary<string, string?> options, TextWriter output)
    {
        if (Value(options, "--hub") is not { } hub || !Uri.TryCreate(hub, UriKind.Absolute, out var uri))
        {
            output.WriteLine("pair needs --hub <the coordinator's address, e.g. https://hub.example.com>.");
            return 1;
        }

        var code = Value(options, "--code");

        if (string.IsNullOrWhiteSpace(code))
        {
            output.Write("Pairing code from the coordinator's console: ");
            code = Console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            output.WriteLine("No pairing code was given.");
            return 1;
        }

        var port = int.TryParse(Value(options, "--port"), out var p) ? p : ChatGptOAuth.DefaultCallbackPort;

        var result = await ChatGptPairing.RunAsync(
            uri,
            code,
            port,
            options.ContainsKey("--allow-http"),
            openBrowser: !options.ContainsKey("--no-browser"),
            output,
            CancellationToken.None);

        output.WriteLine(result.Message);
        return 0;
    }

    private static void Print(ChatGptSession session, string directory, TextWriter output)
    {
        var state = session.State();

        output.WriteLine($"Store:      {directory}");
        output.WriteLine($"Host id:    {session.HostId()}");
        output.WriteLine($"Signed in:  {(state.SignedIn ? $"yes, as {state.Email ?? state.Account}" : "no")}");
        output.WriteLine($"Plan usage: {(state.PlanUsage ? "granted" : "not granted")}");

        if (state.AccessExpiresAtUtc is { } expires)
        {
            output.WriteLine($"Access token renews before {expires:u}");
        }

        if (state.LastError is { } last)
        {
            output.WriteLine($"Last error: {last}");
        }

        foreach (var registration in state.Registrations)
        {
            output.WriteLine($"  {(registration.Active ? "*" : " ")} {registration.Id}  {registration.Email}  "
                             + $"{(registration.SignedIn ? "signed in" : "signed out")}{(registration.PlanUsage ? ", plan usage" : "")}");
        }
    }

    private static string? Value(Dictionary<string, string?> options, string name)
        => options.TryGetValue(name, out var value) ? value : null;

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[args[i]] = hasValue ? args[++i] : null;
        }

        return options;
    }
}
