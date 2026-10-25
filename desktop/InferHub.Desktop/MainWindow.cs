using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace InferHub.Desktop;

/// <summary>
/// The control panel (phase 115): one window, seven tabs, every action a call to the node's control API through
/// <see cref="AppController"/>. Built in code: what a control shows is next to what it does.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppController app;
    private readonly TabControl tabs;
    private readonly TextBlock statusBar = new() { Margin = new Thickness(12, 6), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock header = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock subHeader = new() { Opacity = 0.75 };

    // Overview
    private readonly TextBlock overview = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace") };
    private readonly ComboBox sharing = new() { ItemsSource = new[] { "always", "idle", "paused" }, MinWidth = 160 };
    private readonly TextBlock sharingReason = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private bool settingSharing;
    private readonly TextBox sharingHours = new() { AcceptsReturn = true, MinHeight = 64, MinWidth = 320, TextWrapping = TextWrapping.NoWrap, PlaceholderText = "one line each, e.g. mon-fri 18:00-08:00 · weekends all day (empty: any hour)" };
    private readonly TextBox sharingZone = new() { MinWidth = 200, PlaceholderText = "this machine's zone" };
    private readonly TextBox sharingMinutes = new() { MinWidth = 80, PlaceholderText = "0 = none" };
    private readonly TextBlock sharingTerms = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock sharingModels = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private bool termsShown;

    // Activity
    private readonly TextBlock running = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace") };
    private readonly TextBlock recent = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace") };

    // Models
    private readonly StackPanel models = new() { Spacing = 4 };
    private readonly TextBox pullName = new() { PlaceholderText = "a model to pull, e.g. qwen2.5:0.5b or a Hugging Face link", MinWidth = 380 };
    private readonly TextBlock modelProgress = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

    // Settings
    private readonly StackPanel settingsForm = new() { Spacing = 6 };
    private readonly Dictionary<string, Control> settingInputs = [];
    private readonly Dictionary<string, string> settingTypes = [];
    private readonly TextBox rawFile = new() { AcceptsReturn = true, MinHeight = 260, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), TextWrapping = TextWrapping.NoWrap };

    // Logs
    private readonly TextBox logs = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12 };
    private readonly StringBuilder logText = new();
    private CancellationTokenSource? logFollow;

    // Chat
    private readonly ComboBox chatModel = new() { MinWidth = 240 };
    private readonly TextBox chatPrompt = new() { AcceptsReturn = true, MinHeight = 90, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Ask this node something" };
    private readonly TextBlock chatAnswer = new() { TextWrapping = TextWrapping.Wrap };

    // Connection
    private readonly TextBox url = new() { MinWidth = 320 };
    private readonly TextBox token = new() { PasswordChar = '•', MinWidth = 320, PlaceholderText = "the control token (control.token in the node's data directory)" };
    private readonly TextBox apiKey = new() { PasswordChar = '•', MinWidth = 320, PlaceholderText = "only if the node's local API has keys" };
    private readonly TextBlock embeddedState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox startOnLaunch = new() { Content = "Start this app's own node when the app starts" };
    private readonly CheckBox closeToTray = new() { Content = "Closing the window keeps InferHub in the tray" };

    public MainWindow(AppController app)
    {
        this.app = app;
        Title = "InferHub";
        Width = 1040;
        Height = 720;
        MinWidth = 720;
        MinHeight = 480;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://InferHub.Desktop/Assets/inferhub-256.png")));

        tabs = new TabControl
        {
            Margin = new Thickness(8, 0),
            ItemsSource = new[]
            {
                new TabItem { FontSize = 15, Header = "Overview", Content = Scroll(OverviewTab()) },
                new TabItem { FontSize = 15, Header = "Activity", Content = Scroll(ActivityTab()) },
                new TabItem { FontSize = 15, Header = "Models", Content = Scroll(ModelsTab()) },
                new TabItem { FontSize = 15, Header = "Settings", Content = Scroll(SettingsTab()) },
                new TabItem { FontSize = 15, Header = "Logs", Content = LogsTab() },
                new TabItem { FontSize = 15, Header = "Chat", Content = Scroll(ChatTab()) },
                new TabItem { FontSize = 15, Header = "Connection", Content = Scroll(ConnectionTab()) }
            }
        };

        var top = new StackPanel { Margin = new Thickness(16, 12, 16, 4), Children = { header, subHeader } };
        var bottom = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brushes.Gray, Child = statusBar, Opacity = 0.9 };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(bottom);
        dock.Children.Add(tabs);
        // The window's own background on its content too, so a rendered picture of it is not text on transparency.
        dock[!Avalonia.Controls.Panel.BackgroundProperty] = this[!BackgroundProperty];
        Content = dock;

        header.Text = "InferHub";
        subHeader.Text = "looking for a node on this machine…";

        app.Message += message => Ui(() => statusBar.Text = message);
        app.StatusChanged += status => Ui(() => ShowStatus(status));
        app.ConnectionChanged += () => Ui(OnConnectionChanged);
        app.EmbeddedChanged += () => Ui(ShowEmbedded);
        app.NodeEvent += item => Ui(() => OnNodeEvent(item));

        sharing.SelectionChanged += async (_, _) =>
        {
            if (!settingSharing && sharing.SelectedItem is string mode)
            {
                await app.SetSharingAsync(mode);
            }
        };

        url.Text = app.Settings.Url ?? NodeLocator.DefaultUrl;
        apiKey.Text = app.Settings.ApiKey;
        startOnLaunch.IsChecked = app.Settings.StartEmbeddedOnLaunch;
        closeToTray.IsChecked = app.Settings.CloseToTray;
        ShowEmbedded();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // The tray keeps the app — and its own node — running; Quit is in the tray menu.
        if (app.Settings.CloseToTray && !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    // ---------------------------------------------------------------- tabs

    private Control OverviewTab()
    {
        var restart = Button("Restart the node", async () => await app.RestartAsync());
        var refresh = Button("Refresh", async () => await app.RefreshAsync());

        return Panel(
            Heading("Sharing with the network"),
            Row(new TextBlock { Text = "Mode", VerticalAlignment = VerticalAlignment.Center, Width = 60 }, sharing),
            new TextBlock
            {
                Text = "always: the network may use this node · idle: your own work first, sharing pauses while you use it · paused: the network gets nothing new",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            },
            sharingReason,
            sharingModels,
            Heading("Sharing hours and a daily allowance"),
            sharingTerms,
            Labelled("Hours", sharingHours),
            Labelled("Time zone", sharingZone),
            Labelled("Minutes a day", sharingMinutes),
            new TextBlock
            {
                Text = "Outside these hours, or once the network has had its minutes for the day, the node takes no new network jobs. A job already running always finishes. Your own work never counts.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            },
            Row(Button("Save hours and allowance", SaveSharingTermsAsync)),
            Heading("This node"),
            overview,
            Row(refresh, restart));
    }

    private Control ActivityTab() => Panel(Heading("Running now"), running, Heading("Finished"), recent);

    private Control ModelsTab() =>
        Panel(
            Heading("Models"),
            new TextBlock
            {
                Text = "Shared: the network may use the model. Clear the box to keep it for this machine — your own apps still use it, the network is not told it is here.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            },
            models,
            Heading("Get a model"),
            Row(pullName, Button("Pull", async () => await ModelAction(pullName.Text?.Trim(), "pull"))),
            modelProgress);

    private Control SettingsTab() =>
        Panel(
            new TextBlock
            {
                Text = "These are written to the node's node.local.json and checked by the node before they are saved. Some take effect at once; the others after a restart.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75
            },
            settingsForm,
            Row(Button("Save settings", SaveSettingsAsync), Button("Reload", LoadSettingsAsync), Button("Restart the node", async () => await app.RestartAsync())),
            Heading("node.local.json (advanced)"),
            rawFile,
            Row(Button("Load the file", LoadRawFileAsync), Button("Save the file", SaveRawFileAsync)));

    private Control LogsTab()
    {
        var follow = Button("Follow the log", () => { FollowLogs(); return Task.CompletedTask; });
        var clear = Button("Clear", () => { logText.Clear(); logs.Text = ""; return Task.CompletedTask; });
        var grid = new DockPanel { Margin = new Thickness(8) };
        var buttons = Row(follow, clear);
        DockPanel.SetDock(buttons, Dock.Top);
        grid.Children.Add(buttons);
        grid.Children.Add(logs);
        return grid;
    }

    private Control ChatTab() =>
        Panel(
            new TextBlock { Text = "Ask a model on this node through its own API — the same one any app on this machine can use.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            Row(new TextBlock { Text = "Model", VerticalAlignment = VerticalAlignment.Center, Width = 60 }, chatModel),
            chatPrompt,
            Row(Button("Send", SendChatAsync)),
            new Border { Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, CornerRadius = new CornerRadius(6), Child = chatAnswer });

    private Control ConnectionTab() =>
        Panel(
            Heading("A node on this machine or elsewhere"),
            Labelled("Address", url),
            Labelled("Control token", token),
            Labelled("Local API key", apiKey),
            Row(Button("Connect", async () =>
            {
                app.Settings.ApiKey = string.IsNullOrWhiteSpace(apiKey.Text) ? null : apiKey.Text.Trim();
                app.Save();
                await app.ConnectAsync(url.Text ?? NodeLocator.DefaultUrl, token.Text ?? "");
            }), Button("Look again", app.StartAsync)),
            Heading("This app's own node"),
            embeddedState,
            Row(Button("Start it", app.StartEmbeddedAsync), Button("Stop it", () => { app.StopEmbedded(); return Task.CompletedTask; })),
            startOnLaunch,
            closeToTray,
            Button("Save these choices", () =>
            {
                app.Settings.StartEmbeddedOnLaunch = startOnLaunch.IsChecked == true;
                app.Settings.CloseToTray = closeToTray.IsChecked == true;
                app.Save();
                app.Say("Saved.");
                return Task.CompletedTask;
            }));

    // ---------------------------------------------------------------- state

    private void OnConnectionChanged()
    {
        termsShown = false;

        if (app.Client is { } client)
        {
            subHeader.Text = $"{client.Endpoint.BaseUrl} · {client.Endpoint.Source}";
            _ = LoadSettingsAsync();
            _ = LoadModelsAsync();
            _ = LoadActivityAsync();
            FollowLogs();
        }
        else
        {
            header.Text = "InferHub";
            subHeader.Text = "not connected to a node";
            logFollow?.Cancel();
        }
    }

    private void ShowStatus(JsonObject status)
    {
        if (app.LastError is { } error)
        {
            subHeader.Text = $"{app.Client?.Endpoint.BaseUrl} · not answering: {error}";
            return;
        }

        var node = status["node"];
        var network = status["network"];
        var shared = status["sharing"];
        var jobs = status["jobs"];
        header.Text = $"{Text(node?["name"])} — InferHub node {Text(node?["version"])}";

        settingSharing = true;
        sharing.SelectedItem = Text(shared?["mode"]);
        settingSharing = false;

        var held = shared?["held"] is JsonValue h && h.TryGetValue<bool>(out var holds) ? holds : (bool?)null;
        sharingReason.Text = (shared?["paused"]?.GetValue<bool>() == true ? "Not taking network jobs now: " + Text(shared?["reason"]) : "Taking network jobs.")
                             + (held == false ? "  This network's coordinator is older than 3.76 and cannot hold a pause — update it." : "");
        sharingTerms.Text = SharingTerms.Describe(shared);
        sharingModels.Text = SharedModels.Describe(status);

        // Filled once per connection, so a poll never overwrites what the owner is typing.
        if (!termsShown)
        {
            termsShown = true;
            sharingHours.Text = SharingTerms.HoursText(shared);
            sharingZone.Text = shared?["timeZone"]?.GetValue<string>() ?? "";
            sharingMinutes.Text = shared?["allowance"]?["minutes"]?.ToString() ?? "0";
        }

        var b = new StringBuilder();
        b.AppendLine($"Network       {Text(network?["state"])}{(network?["coordinator"] is { } hub ? "  " + Text(hub) : "")}");
        b.AppendLine($"Jobs          {Text(jobs?["running"])} running ({Text(jobs?["local"])} yours, {Text(jobs?["network"])} the network's)");
        b.AppendLine($"Up            {Duration(node?["uptimeSeconds"])}");
        b.AppendLine($"Backend       {Text(status["backend"]?["name"])}  {Text(status["backend"]?["endpoint"])}");

        if (status["engines"] is JsonArray engines)
        {
            foreach (var engine in engines)
            {
                b.AppendLine($"  engine      {Text(engine?["name"]),-14} {Text(engine?["state"]),-10} {Text(engine?["type"])}");
            }
        }

        if (status["models"] is JsonArray list)
        {
            b.AppendLine($"Models        {list.Count}: {string.Join(", ", list.Take(8).Select(m => Text(m?["name"])))}{(list.Count > 8 ? ", …" : "")}");
        }

        if (status["limits"]?["groups"] is JsonArray groups && groups.Count > 0)
        {
            foreach (var group in groups)
            {
                b.AppendLine($"  limit       {Text(group?["group"]),-10} {Text(group?["memoryMiB"])} MiB · VRAM {Text(group?["vramMiB"])} MiB · told {Text(group?["told"])}");
            }
        }

        b.AppendLine($"Restart       {(status["restart"]?["supported"]?.GetValue<bool>() == true ? Text(status["restart"]?["why"]) : "not available: " + Text(status["restart"]?["why"]))}");
        b.AppendLine($"Settings file {Text(node?["localSettingsFile"])}");
        overview.Text = b.ToString();

        if (status["models"] is JsonArray names)
        {
            var current = chatModel.SelectedItem as string;
            var items = names.Select(m => Text(m?["name"])).Where(n => n.Length > 0).ToList();

            if (!items.SequenceEqual(chatModel.ItemsSource?.Cast<string>() ?? []))
            {
                chatModel.ItemsSource = items;
                chatModel.SelectedItem = current is not null && items.Contains(current) ? current : items.FirstOrDefault();
            }
        }
    }

    private void ShowEmbedded()
    {
        embeddedState.Text = !app.Embedded.Available
            ? "This build carries no node. (A release carries one, in node/ beside the app.)"
            : app.Embedded.Running
                ? $"Running on {app.Embedded.Url}. Its data and settings: {app.Embedded.DataDirectory}"
                : $"Not running. It would listen on {app.Embedded.Url} and keep its data in {app.Embedded.DataDirectory}.";
    }

    private void OnNodeEvent(ServerEvent item)
    {
        switch (item.Type)
        {
            case "job.started" or "job.finished":
                _ = LoadActivityAsync();
                break;
            case "model.progress":
                var done = item.Data?["done"]?.GetValue<bool>() == true;
                modelProgress.Text = $"{Text(item.Data?["kind"])} {Text(item.Data?["modelName"])}: {Text(item.Data?["status"])}"
                                     + (item.Data?["percent"] is JsonValue p ? $" {p}%" : "")
                                     + (item.Data?["error"] is { } e && Text(e).Length > 0 ? $" — {Text(e)}" : "");

                if (done)
                {
                    _ = LoadModelsAsync();
                }

                break;
            case "sharing.changed" or "settings.saved":
                _ = app.RefreshAsync();
                break;
            case "node.restarting":
                app.Say("The node is restarting…");
                break;
        }
    }

    private async Task LoadActivityAsync() =>
        await app.Run(async client =>
        {
            var activity = await client.ActivityAsync();
            running.Text = Lines(activity["running"] as JsonArray, running: true) is { Length: > 0 } now ? now : "nothing";
            recent.Text = Lines(activity["recent"] as JsonArray, running: false) is { Length: > 0 } done ? done : "nothing yet";
        });

    private static string Lines(JsonArray? entries, bool running)
    {
        var b = new StringBuilder();

        foreach (var e in entries ?? [])
        {
            var who = Text(e?["source"]) == "mesh" ? "network" : "you";
            var at = e?["startedAt"]?.GetValue<DateTimeOffset>().ToLocalTime().ToString("HH:mm:ss") ?? "";
            b.AppendLine(running
                ? $"{at}  {who,-8} {Text(e?["kind"]),-10} {Text(e?["model"])}"
                : $"{at}  {who,-8} {Text(e?["kind"]),-10} {Text(e?["model"]),-30} {Text(e?["outcome"]),-8} {Text(e?["seconds"])} s");
        }

        return b.ToString();
    }

    private async Task LoadModelsAsync() =>
        await app.Run(async client =>
        {
            var reply = await client.ModelsAsync();
            models.Children.Clear();
            var manages = reply["managesModels"]?.GetValue<bool>() == true;

            foreach (var model in reply["models"] as JsonArray ?? [])
            {
                var name = Text(model?["name"]);
                var row = Row(new TextBlock { Text = $"{name}  {Size(model?["size"])}", VerticalAlignment = VerticalAlignment.Center, MinWidth = 380 });

                // Phase 117: a 3.77 node says nothing about sharing a model, and then there is no box to tick.
                if (model?["shared"] is JsonValue sharedValue && sharedValue.TryGetValue<bool>(out var isShared))
                {
                    var box = new CheckBox { Content = "Shared", IsChecked = isShared, VerticalAlignment = VerticalAlignment.Center };
                    box.IsCheckedChanged += async (_, _) => await ShareModelAsync(reply["sharing"], name, box);
                    row.Children.Add(box);
                }

                if (manages)
                {
                    row.Children.Add(Button("Warm", async () => await ModelAction(name, "warm")));
                    row.Children.Add(Button("Unload", async () => await ModelAction(name, "unload")));
                    row.Children.Add(Button("Delete", async () =>
                    {
                        // The engine's own store — for an app's own node, often the same Ollama other programs use.
                        if (await ConfirmAsync($"Delete {name} from this node's engine? It is removed from disk for everything that uses that engine."))
                        {
                            await ModelAction(name, "delete");
                        }
                    }));
                }

                models.Children.Add(row);
            }

            foreach (var engine in new[] { "colibri", "strata" })
            {
                if (reply[engine]?["models"] is not JsonArray catalog)
                {
                    continue;
                }

                models.Children.Add(Heading($"{engine} — loaded on request"));

                foreach (var model in catalog)
                {
                    var name = Text(model?["name"]);
                    var state = Text(model?["state"]);
                    var row = Row(new TextBlock { Text = $"{name}  ({state}{(model?["pinned"]?.GetValue<bool>() == true ? ", pinned" : "")})", VerticalAlignment = VerticalAlignment.Center, MinWidth = 380 });
                    row.Children.Add(Button("Load", async () => await app.Run(async c => app.Say(Note(await c.CatalogAsync(engine, name, load: true))))));
                    row.Children.Add(Button("Unload", async () => await app.Run(async c => app.Say(Note(await c.CatalogAsync(engine, name, load: false))))));
                    models.Children.Add(row);
                }
            }

            if (models.Children.Count == 0)
            {
                models.Children.Add(new TextBlock { Text = "The node lists no models." });
            }
        });

    private async Task ShareModelAsync(JsonNode? lists, string name, CheckBox box)
    {
        var shared = box.IsChecked == true;
        var (include, exclude, error) = SharedModels.Toggle(lists, name, shared);

        if (error is not null)
        {
            app.Say(error);
            await LoadModelsAsync();
            return;
        }

        await app.SetSharedModelsAsync(name, shared, include, exclude);
        await LoadModelsAsync();
    }

    private async Task ModelAction(string? name, string action)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            app.Say("Name a model first.");
            return;
        }

        await app.Run(async client =>
        {
            await client.ModelActionAsync(name, action);
            modelProgress.Text = $"{action} {name}: started";
        });
    }

    private async Task LoadSettingsAsync() =>
        await app.Run(async client =>
        {
            var reply = await client.SettingsAsync();
            settingsForm.Children.Clear();
            settingInputs.Clear();
            settingTypes.Clear();
            string? group = null;

            foreach (var key in reply["keys"] as JsonArray ?? [])
            {
                var name = Text(key?["key"]);
                var type = Text(key?["type"]);
                var value = key?["value"];

                if (Text(key?["group"]) is var g && g != group)
                {
                    group = g;
                    settingsForm.Children.Add(Heading(GroupTitle(g)));
                }

                Control input = type switch
                {
                    "bool" => new CheckBox { IsChecked = value is JsonValue v && bool.TryParse(v.ToString(), out var on) && on },
                    // An unset key shows the node's default — the first value the node lists.
                    "enum" => new ComboBox { ItemsSource = (key?["values"] as JsonArray)?.Select(x => Text(x)).ToList(), SelectedItem = value is null ? Text((key?["values"] as JsonArray)?.FirstOrDefault()) : Text(value), MinWidth = 160 },
                    "json" => new TextBox { Text = value?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "", AcceptsReturn = true, MinWidth = 420, MinHeight = 60, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace") },
                    "secret" => new TextBox { PasswordChar = '•', PlaceholderText = value is null ? "not set" : "set — type to replace", MinWidth = 320 },
                    _ => new TextBox { Text = value is null ? "" : Text(value), MinWidth = 320 }
                };

                settingInputs[name] = input;
                settingTypes[name] = type;
                var live = key?["live"]?.GetValue<bool>() == true ? "applies at once" : "after a restart";
                settingsForm.Children.Add(new StackPanel
                {
                    Spacing = 2,
                    Margin = new Thickness(0, 4),
                    Children =
                    {
                        new TextBlock { Text = $"{name}   ({live})", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = Text(key?["about"]), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 },
                        input
                    }
                });
            }

            original = Snapshot();
        });

    private Dictionary<string, string?> original = [];

    private Dictionary<string, string?> Snapshot() =>
        settingInputs.ToDictionary(p => p.Key, p => p.Value switch
        {
            CheckBox c => c.IsChecked == true ? "true" : "false",
            ComboBox c => c.SelectedItem as string,
            TextBox t => t.Text,
            _ => null
        });

    private async Task SaveSharingTermsAsync()
    {
        var text = sharingMinutes.Text?.Trim();

        if (!int.TryParse(string.IsNullOrEmpty(text) ? "0" : text, out var minutes) || minutes < 0)
        {
            app.Say("Minutes a day is a whole number: 0 for no allowance.");
            return;
        }

        await app.SetSharingTermsAsync(SharingTerms.Lines(sharingHours.Text), sharingZone.Text?.Trim(), minutes);
    }

    private async Task SaveSettingsAsync()
    {
        var now = Snapshot();
        var set = new Dictionary<string, JsonNode?>();
        var remove = new List<string>();

        foreach (var (name, value) in now)
        {
            if (original.GetValueOrDefault(name) == value)
            {
                continue;
            }

            var type = settingTypes[name];

            if (string.IsNullOrWhiteSpace(value))
            {
                if (type != "secret")
                {
                    remove.Add(name);
                }

                continue;
            }

            try
            {
                set[name] = type switch
                {
                    "bool" => JsonValue.Create(value == "true"),
                    "int" => JsonValue.Create(long.Parse(value)),
                    "json" => JsonNode.Parse(value),
                    _ => JsonValue.Create(value)
                };
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                app.Say($"{name}: {ex.Message}");
                return;
            }
        }

        if (set.Count == 0 && remove.Count == 0)
        {
            app.Say("Nothing changed.");
            return;
        }

        await app.Run(async client =>
        {
            var reply = await client.SaveSettingsAsync(set, remove);
            var restart = reply["restartRequired"]?.GetValue<bool>() == true;
            app.Say(restart
                ? $"Saved. A restart applies {string.Join(", ", (reply["restartFor"] as JsonArray ?? []).Select(x => Text(x)))} — use Restart the node."
                : "Saved, and in force now.");
            await LoadSettingsAsync();
        });
    }

    private async Task LoadRawFileAsync() =>
        await app.Run(async client => rawFile.Text = (await client.SettingsFileAsync()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private async Task SaveRawFileAsync()
    {
        JsonObject? file;

        try
        {
            file = JsonNode.Parse(rawFile.Text ?? "{}") as JsonObject;
        }
        catch (JsonException ex)
        {
            app.Say($"That is not JSON: {ex.Message}");
            return;
        }

        if (file is null)
        {
            app.Say("The file must be a JSON object.");
            return;
        }

        await app.Run(async client =>
        {
            await client.SaveSettingsFileAsync(file);
            app.Say("Saved node.local.json. A restart applies what is not read live.");
        });
    }

    private void FollowLogs()
    {
        logFollow?.Cancel();

        if (app.Client is not { } client)
        {
            return;
        }

        logFollow = new CancellationTokenSource();
        var token = logFollow.Token;
        var endpoint = client.Endpoint;

        _ = Task.Run(async () =>
        {
            try
            {
                using var stream = new NodeControlClient(endpoint);

                await foreach (var line in stream.LogStreamAsync(0, token))
                {
                    var text = $"{line.Data?["at"]?.GetValue<DateTimeOffset>().ToLocalTime():HH:mm:ss} {Text(line.Data?["level"])[..Math.Min(4, Text(line.Data?["level"]).Length)],-4} {Text(line.Data?["message"])}";
                    Ui(() =>
                    {
                        logText.AppendLine(text);

                        if (logText.Length > 400_000)
                        {
                            logText.Remove(0, logText.Length - 300_000);
                        }

                        logs.Text = logText.ToString();
                        logs.CaretIndex = logs.Text.Length;
                    });
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Ui(() => app.Say($"The log stream ended: {ex.Message}"));
            }
        }, token);
    }

    private async Task SendChatAsync()
    {
        if (chatModel.SelectedItem is not string model || string.IsNullOrWhiteSpace(chatPrompt.Text))
        {
            app.Say("Pick a model and write something.");
            return;
        }

        chatAnswer.Text = "…";
        await app.Run(async client => chatAnswer.Text = await client.ChatAsync(model, chatPrompt.Text!, app.Settings.ApiKey));
    }

    /// <summary>
    /// Renders this window, tab by tab, into PNGs (<c>INFERHUB_DESKTOP_SCREENSHOTS=dir</c>): pictures of the app for its
    /// docs and checks, taken from the app itself rather than from the screen it shares with everything else.
    /// </summary>
    public async Task SaveScreenshotsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var names = new[] { "overview", "activity", "models", "settings", "logs", "chat", "connection" };

        for (var i = 0; i < names.Length; i++)
        {
            tabs.SelectedIndex = i;
            await Task.Delay(1200);

            if (Content is Control root)
            {
                var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96, 96));
                bitmap.Render(root);
                bitmap.Save(Path.Combine(directory, $"desktop-{i + 1}-{names[i]}.png"));
            }
        }

        tabs.SelectedIndex = 0;
    }

    /// <summary>A yes/no question in a small window of its own — Avalonia ships no message box.</summary>
    private async Task<bool> ConfirmAsync(string question)
    {
        var answer = false;
        var dialog = new Window
        {
            Title = "InferHub",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "Delete" };
        var no = new Button { Content = "Cancel" };
        yes.Click += (_, _) => { answer = true; dialog.Close(); };
        no.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap }, Row(no, yes) }
        };
        await dialog.ShowDialog(this);
        return answer;
    }

    // ---------------------------------------------------------------- helpers

    private static void Ui(Action action) => Dispatcher.UIThread.Post(action);

    private static ScrollViewer Scroll(Control content) => new() { Content = content };

    private static StackPanel Panel(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(16, 12) };

        foreach (var child in children)
        {
            panel.Children.Add(child);
        }

        return panel;
    }

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static Control Labelled(string label, Control input) =>
        Row(new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center }, input);

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 2) };

    private static Button Button(string text, Func<Task> click)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;

            try
            {
                await click();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static string GroupTitle(string group) => group switch
    {
        "node" => "This node",
        "sharing" => "Sharing with the network",
        "limits" => "Limits per runtime",
        "network" => "The network (a coordinator)",
        "local" => "This node's own API",
        "engines" => "Engines",
        "updates" => "Updates",
        _ => group
    };

    private static string Note(JsonObject reply) =>
        (reply["refusals"] as JsonArray is { Count: > 0 } refused ? "Refused: " + string.Join("; ", refused.Select(r => Text(r))) : "Done.")
        + (reply["note"] is { } note ? $" ({Text(note)})" : "");

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        JsonArray array => string.Join(", ", array.Select(Text)),
        _ => node.ToJsonString()
    };

    private static string Duration(JsonNode? seconds)
    {
        if (seconds is not JsonValue v || !v.TryGetValue<double>(out var s))
        {
            return "";
        }

        var span = TimeSpan.FromSeconds(s);
        return span.TotalDays >= 1 ? $"{(int)span.TotalDays} d {span.Hours} h" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min {span.Seconds} s";
    }

    private static string Size(JsonNode? bytes) =>
        bytes is JsonValue v && v.TryGetValue<long>(out var b) && b > 0 ? $"{b / 1e9:0.0} GB" : "";
}
