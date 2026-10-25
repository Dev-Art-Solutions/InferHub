using System.Text.Json.Nodes;

namespace InferHub.Desktop;

/// <summary>
/// Everything the window shows and does, without a control in it (phase 115): which node, its status every few seconds,
/// its events as they come, the app's own node. The window subscribes; every event may arrive on a background thread.
/// </summary>
public sealed class AppController : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly NodeLocator locator = new();
    private CancellationTokenSource? connection;
    private long lastEvent;

    public AppController(DesktopSettings settings)
    {
        Settings = settings;
        Embedded = new EmbeddedNode();
        Embedded.StateChanged += message =>
        {
            Say(message);
            EmbeddedChanged?.Invoke();
        };
    }

    public DesktopSettings Settings { get; }

    public EmbeddedNode Embedded { get; }

    public NodeControlClient? Client { get; private set; }

    public JsonObject? Status { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>A sentence for the status bar: what the app just did or could not do.</summary>
    public event Action<string>? Message;

    public event Action? ConnectionChanged;

    public event Action<JsonObject>? StatusChanged;

    public event Action<ServerEvent>? NodeEvent;

    public event Action? EmbeddedChanged;

    /// <summary>115 D2: attach to a node that answers; else run the app's own when asked to.</summary>
    public async Task StartAsync()
    {
        if (Settings.Mode == "embedded" || Settings.StartEmbeddedOnLaunch)
        {
            await StartEmbeddedAsync();
            return;
        }

        var found = await locator.LocateAsync(Settings, lifetime.Token);

        if (found.Endpoint is { } endpoint)
        {
            Attach(endpoint);
            return;
        }

        var why = found.Unreadable.Count > 0
            ? $"A node's token is for administrators only ({string.Join(", ", found.Unreadable)}): paste it under Connection, or run this app's own node."
            : Embedded.Available
                ? "No node answered on this machine. Start this app's own node under Connection, or connect to one."
                : "No node answered on this machine, and this build carries none. Connect to one under Connection.";
        Say(why);
        ConnectionChanged?.Invoke();
    }

    public async Task<bool> ConnectAsync(string url, string token)
    {
        var endpoint = new NodeEndpoint(new Uri(url.Trim()), token.Trim(), "entered in this app");

        if (!await NodeLocator.ProbeAsync(endpoint, lifetime.Token))
        {
            Say($"No node answered at {url} with that token.");
            return false;
        }

        Settings.Url = url.Trim();
        Settings.Token = token.Trim();
        Settings.Mode = "attach";
        Save();
        Attach(endpoint);
        return true;
    }

    public async Task StartEmbeddedAsync()
    {
        if (!Embedded.Available)
        {
            Say("This build of the app carries no node (node/ beside the app). Connect to one instead.");
            return;
        }

        try
        {
            Embedded.Start();
        }
        catch (Exception ex)
        {
            Say($"The app's own node did not start: {ex.Message}");
            return;
        }

        // The node writes its token on first start; wait for it and for the API to answer.
        for (var i = 0; i < 120 && !lifetime.IsCancellationRequested; i++)
        {
            if (File.Exists(Embedded.TokenFile)
                && File.ReadAllText(Embedded.TokenFile).Trim() is { Length: > 0 } token
                && await NodeLocator.ProbeAsync(new NodeEndpoint(Embedded.Url, token, "this app's own node"), lifetime.Token))
            {
                Attach(new NodeEndpoint(Embedded.Url, token, "this app's own node"));
                return;
            }

            await Task.Delay(500, lifetime.Token);
        }

        Say($"The app's own node started but its API did not answer; see {Embedded.ConsoleLog}.");
    }

    public void StopEmbedded()
    {
        Embedded.Stop();

        if (Client?.Endpoint.Source == "this app's own node")
        {
            Detach();
        }
    }

    public async Task SetSharingAsync(string mode) =>
        await Run(async client =>
        {
            await client.SetSharingAsync(mode, lifetime.Token);
            Say(mode switch
            {
                "paused" => "Sharing paused: the network gets no new jobs from this node.",
                "idle" => "Sharing when you are not using it.",
                _ => "Sharing with the network."
            });
            await RefreshAsync();
        });

    public async Task SetSharingTermsAsync(IReadOnlyList<string> hours, string? timeZone, int dailyNetworkMinutes) =>
        await Run(async client =>
        {
            await client.SetSharingTermsAsync(hours, timeZone, dailyNetworkMinutes, lifetime.Token);
            Say(hours.Count == 0 && dailyNetworkMinutes == 0
                ? "Sharing at any hour, with no daily allowance."
                : "Sharing hours and allowance saved; the node applies them at once.");
            await RefreshAsync();
        });

    public async Task SetSharedModelsAsync(string model, bool shared, IReadOnlyList<string> include, IReadOnlyList<string> exclude) =>
        await Run(async client =>
        {
            await client.SetSharedModelsAsync(include, exclude, lifetime.Token);
            Say(shared
                ? $"{model} is shared with the network."
                : $"{model} is kept for this machine: the network is no longer told it is here.");
            await RefreshAsync();
        });

    public async Task RestartAsync() =>
        await Run(async client =>
        {
            var reply = await client.RestartAsync(cancellationToken: lifetime.Token);
            Say($"Restarting ({reply["supervisor"]?.GetValue<string>()}).");
        });

    /// <summary>Runs a call against the node and turns its refusal into the status bar's sentence.</summary>
    public async Task Run(Func<NodeControlClient, Task> call)
    {
        if (Client is not { } client)
        {
            Say("Not connected to a node.");
            return;
        }

        try
        {
            await call(client);
        }
        catch (NodeControlException ex)
        {
            Say(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            Say($"The node did not answer: {ex.Message}");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task RefreshAsync()
    {
        if (Client is not { } client)
        {
            return;
        }

        try
        {
            Status = await client.StatusAsync(lifetime.Token);
            LastError = null;
            StatusChanged?.Invoke(Status);
        }
        catch (Exception ex) when (ex is HttpRequestException or NodeControlException or TaskCanceledException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                LastError = ex.Message;
                StatusChanged?.Invoke(Status ?? []);
            }
        }
    }

    public void Save()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Could not save this app's settings: {ex.Message}");
        }
    }

    public void Say(string message) => Message?.Invoke(message);

    private void Attach(NodeEndpoint endpoint)
    {
        Detach();
        Client = new NodeControlClient(endpoint);
        connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = connection.Token;
        Say($"Connected to {endpoint.BaseUrl} ({endpoint.Source}).");
        ConnectionChanged?.Invoke();

        _ = Task.Run(() => PollAsync(token), token);
        _ = Task.Run(() => FollowEventsAsync(endpoint, token), token);
    }

    private void Detach()
    {
        connection?.Cancel();
        connection?.Dispose();
        connection = null;
        Client?.Dispose();
        Client = null;
        Status = null;
        ConnectionChanged?.Invoke();
    }

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await RefreshAsync();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The node's events, followed for as long as it is the node; a dropped stream (a restart) is picked up again.</summary>
    private async Task FollowEventsAsync(NodeEndpoint endpoint, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new NodeControlClient(endpoint);

                await foreach (var item in client.EventsAsync(lastEvent, token))
                {
                    lastEvent = Math.Max(lastEvent, item.Id);
                    NodeEvent?.Invoke(item);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // The node went away (a restart, a crash): the poll says so; try the stream again shortly.
                lastEvent = 0;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (lifetime.IsCancellationRequested)
        {
            return;
        }

        lifetime.Cancel();
        Detach();
        Embedded.Dispose();
    }
}
