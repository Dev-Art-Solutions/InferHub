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
            Row(pullName, Button("Pull", async () => await ModelAction(pullName.Text?.Trim(), "pull")), Button("Check fit…", () => FitDialogAsync(pullName.Text?.Trim())), Button("Browse models…", () => CatalogDialogAsync())),
            new TextBlock
            {
                Text = "Check fit: choose the context you need, and the node says which of its engines can run the model, where it would sit and why — then installs it there with that context. Browse models: what is trending and popular on Hugging Face and in Ollama's library, what fits this machine first.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7
            },
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

        // Phase 118: "link|context" — the Check fit dialog for that model, after the tabs.
        if (Environment.GetEnvironmentVariable("INFERHUB_DESKTOP_SCREENSHOT_FIT") is { Length: > 0 } fit)
        {
            var parts = fit.Split('|');
            tabs.SelectedIndex = 2;
            await FitDialogAsync(parts[0], parts.Length > 1 ? parts[1] : null, Path.Combine(directory, "desktop-8-check-fit.png"));
        }

        // Phase 119: "1" — the Browse models dialog as it opens, after the tabs.
        if (Environment.GetEnvironmentVariable("INFERHUB_DESKTOP_SCREENSHOT_CATALOG") is { Length: > 0 })
        {
            tabs.SelectedIndex = 2;
            await CatalogDialogAsync(Path.Combine(directory, "desktop-9-browse-models.png"));
        }

        tabs.SelectedIndex = 0;
    }

    /// <summary>
    /// Phase 119: the "Browse models" dialog — trending and popular models from Hugging Face and Ollama's library,
    /// ordered by the node by what fits this machine. Nothing installs from here: each model opens Check fit, where the
    /// context is chosen.
    /// </summary>
    private async Task CatalogDialogAsync(string? screenshot = null)
    {
        var source = new ComboBox { ItemsSource = CatalogChoices.Sources, SelectedIndex = 0, MinWidth = 120 };
        var sort = new ComboBox { ItemsSource = CatalogChoices.Sorts, SelectedIndex = 0, MinWidth = 110 };
        var kind = new ComboBox { ItemsSource = CatalogChoices.Kinds, SelectedIndex = 0, MinWidth = 90 };
        var search = new TextBox { Width = 200, PlaceholderText = "search, e.g. qwen" };
        var fitsOnly = new CheckBox { Content = "fits only" };
        var results = new StackPanel { Spacing = 6 };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

        var dialog = new Window
        {
            Title = "Browse models — what fits this node first",
            Width = 900,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        async Task ListAsync()
        {
            state.Text = "Asking the node (it reads the lists from Hugging Face and ollama.com, and each repository's file sizes)…";
            results.Children.Clear();
            JsonObject? reply = null;

            await app.Run(async client =>
            {
                reply = await client.CatalogAsync(
                    source.SelectedItem as string ?? "all", sort.SelectedItem as string ?? "trending", kind.SelectedItem as string ?? "text",
                    search.Text, fitsOnly.IsChecked == true);
            });

            if (reply is null)
            {
                state.Text = "The node did not answer; see the status bar. (Browse needs a 3.80 node.)";
                return;
            }

            state.Text = Text(reply["note"]);

            foreach (var problem in CatalogChoices.Problems(reply))
            {
                results.Children.Add(new TextBlock { Text = problem, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed });
            }

            var rows = CatalogChoices.Rows(reply);

            if (rows.Count == 0)
            {
                results.Children.Add(new TextBlock { Text = "Nothing listed for this choice.", TextWrapping = TextWrapping.Wrap });
            }

            foreach (var row in rows)
            {
                var link = row.Link;
                var check = new Button { Content = "Check fit…" };
                check.Click += async (_, _) => { dialog.Close(); await FitDialogAsync(link); };
                var line = Row(check);

                if (row.Sizes.Count > 1)
                {
                    var sizes = new ComboBox
                    {
                        ItemsSource = row.Sizes.Select(s => s.Label).ToArray(),
                        SelectedIndex = Math.Max(0, row.Sizes.ToList().FindIndex(s => s.Link == row.Link)),
                        MinWidth = 260
                    };
                    sizes.SelectionChanged += (_, _) => link = sizes.SelectedIndex >= 0 ? row.Sizes[sizes.SelectedIndex].Link : row.Link;
                    line.Children.Insert(0, sizes);
                }

                results.Children.Add(new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = Brushes.Gray,
                    Padding = new Thickness(0, 6, 0, 0),
                    Child = new StackPanel
                    {
                        Spacing = 3,
                        Children =
                        {
                            new TextBlock { Text = row.Headline, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Opacity = row.Class == "too-big" ? 0.6 : 1 },
                            new TextBlock { Text = row.Details, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 },
                            new TextBlock { Text = row.Why, TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12 },
                            line
                        }
                    }
                });
            }
        }

        var list = Button("List", ListAsync);
        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    Row(source, sort, kind, search, fitsOnly, list),
                    new TextBlock { Text = "Nothing downloads from this list: each model opens Check fit, where the context — and so the engine — is chosen.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 },
                    state,
                    results
                }
            }
        };

        if (screenshot is not null)
        {
            dialog.Show(this);
            await ListAsync();
            await Task.Delay(1500);

            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96)))
            {
                bitmap.Render(dialog);
                bitmap.Save(screenshot);
            }

            dialog.Close();
            return;
        }

        _ = ListAsync();
        await dialog.ShowDialog(this);
    }

    /// <summary>
    /// Phase 118: the "Check fit" dialog. The context is asked first — there is no minimum, the owner says what they
    /// need — and the node's answer for it picks the engine; the arithmetic is shown, never only a verdict.
    /// </summary>
    private async Task FitDialogAsync(string? initial, string? initialContext = null, string? screenshot = null)
    {
        var link = new TextBox { Text = initial ?? "", MinWidth = 460, PlaceholderText = "a Hugging Face link, owner/repo, or an Ollama name like qwen2.5:7b" };
        var context = new TextBox { Text = FitChoices.FormatContext(8192), Width = 110, PlaceholderText = "e.g. 32k" };
        var picks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var results = new StackPanel { Spacing = 6 };
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        JsonObject? answer = null;

        var dialog = new Window
        {
            Title = "Will it fit on this node?",
            Width = 860,
            Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        void ShowPicks(int? trained)
        {
            picks.Children.Clear();

            foreach (var pick in FitChoices.Picks(trained))
            {
                var tokens = pick;
                var button = new Button { Content = FitChoices.FormatContext(tokens), Padding = new Thickness(6, 2) };
                button.Click += async (_, _) => { context.Text = FitChoices.FormatContext(tokens); await EstimateAsync(); };
                picks.Children.Add(button);
            }
        }

        async Task EstimateAsync()
        {
            var text = link.Text?.Trim();
            var tokens = FitChoices.ParseContext(context.Text);

            if (string.IsNullOrEmpty(text) || tokens is null)
            {
                state.Text = string.IsNullOrEmpty(text) ? "Give a model first." : "The context is a number of tokens: 8192, 32k, 128K, 1M.";
                return;
            }

            state.Text = "Asking the node (it reads the model's header from Hugging Face or Ollama, not the model)…";
            results.Children.Clear();

            await app.Run(async client =>
            {
                answer = await client.FitAsync(text, tokens);
                state.Text = "";
                Show(answer, tokens.Value);
            });

            if (answer is null && state.Text.StartsWith("Asking", StringComparison.Ordinal))
            {
                state.Text = "The node did not answer; see the status bar.";
            }
        }

        void Show(JsonObject reply, int tokens)
        {
            results.Children.Clear();
            ShowPicks(reply["model"]?["trainedContext"] is JsonValue t && t.TryGetValue<int>(out var trained) ? trained : null);
            results.Children.Add(new TextBlock { Text = FitChoices.Model(reply), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            results.Children.Add(new TextBlock { Text = "This machine: " + FitChoices.Machine(reply), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

            foreach (var note in reply["model"]?["notes"] as JsonArray ?? [])
            {
                results.Children.Add(new TextBlock { Text = Text(note), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            }

            var rows = FitChoices.Rows(reply);

            if (rows.Count == 0)
            {
                results.Children.Add(new TextBlock { Text = "No engine on any node can run this model as it is.", TextWrapping = TextWrapping.Wrap });
            }

            var first = true;

            foreach (var row in rows)
            {
                var headline = new TextBlock { Text = (first && row.Fits && row.Available ? "Best here: " : "") + row.Headline, TextWrapping = TextWrapping.Wrap, FontWeight = first ? FontWeight.SemiBold : FontWeight.Normal };
                var why = new TextBlock { Text = string.Join("\n", row.Why.Select(w => "· " + w)), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 };
                var variant = row.Variant;
                var install = new Button { Content = row.Fits ? "Install" : "Install anyway", IsEnabled = row.Available };
                var line = Row(install);

                if (row.OtherVariants.Count > 0)
                {
                    var sizes = new ComboBox { ItemsSource = new[] { row.Variant }.Concat(row.OtherVariants).ToArray(), SelectedIndex = 0, MinWidth = 160 };
                    sizes.SelectionChanged += (_, _) =>
                    {
                        variant = sizes.SelectedItem as string ?? row.Variant;

                        if (FitChoices.Option(reply, row.Engine, variant) is { } option)
                        {
                            headline.Text = FitChoices.Headline(option);
                            why.Text = string.Join("\n", (option["why"] as JsonArray ?? []).Select(w => "· " + Text(w)));
                        }
                    };
                    line.Children.Insert(0, sizes);
                }

                install.Click += async (_, _) =>
                {
                    var fits = FitChoices.Option(reply, row.Engine, variant)?["fits"]?.GetValue<bool>() == true;

                    if (!fits && !await ConfirmAsync($"The node's estimate says {FitChoices.Title(row.Engine)} / {variant} does not fit at {FitChoices.FormatContext(tokens)} tokens. Download it anyway?", "Download"))
                    {
                        return;
                    }

                    await app.Run(async client =>
                    {
                        var started = await client.FitInstallAsync(Text(reply["link"]), row.Engine, variant, tokens, force: !fits);
                        modelProgress.Text = $"installing {Text(started["model"])} for {FitChoices.Title(row.Engine)} with a {FitChoices.FormatContext(tokens)} context — progress below";
                        dialog.Close();
                    });
                };

                results.Children.Add(new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = Brushes.Gray,
                    Padding = new Thickness(0, 6, 0, 0),
                    Child = new StackPanel { Spacing = 4, Children = { headline, why, line } }
                });
                first = false;
            }

            foreach (var refused in FitChoices.Refused(reply))
            {
                results.Children.Add(new TextBlock { Text = "Not offered — " + refused, TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12 });
            }
        }

        ShowPicks(null);
        var estimate = Button("Estimate", EstimateAsync);
        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    Labelled("Model", link),
                    Labelled("Context", Row(context, estimate)),
                    picks,
                    new TextBlock { Text = "No minimum: the context you choose is what decides the engine — more context is more memory beside the weights.", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 },
                    state,
                    results
                }
            }
        };

        if (screenshot is not null)
        {
            // INFERHUB_DESKTOP_SCREENSHOT_FIT: the dialog rendered by the app itself, as the tabs are.
            context.Text = initialContext ?? context.Text;
            dialog.Show(this);
            await EstimateAsync();
            await Task.Delay(1500);

            using (var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)dialog.Bounds.Width, (int)dialog.Bounds.Height), new Vector(96, 96)))
            {
                bitmap.Render(dialog);
                bitmap.Save(screenshot);
            }

            dialog.Close();
            return;
        }

        if (!string.IsNullOrEmpty(initial))
        {
            _ = EstimateAsync();
        }

        await dialog.ShowDialog(this);
    }

    /// <summary>A yes/no question in a small window of its own — Avalonia ships no message box.</summary>
    private async Task<bool> ConfirmAsync(string question, string yesText = "Delete")
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
        var yes = new Button { Content = yesText };
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
