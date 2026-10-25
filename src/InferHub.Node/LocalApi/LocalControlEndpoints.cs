using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InferHub.Node.Backends;
using InferHub.Node.Configuration;
using InferHub.Node.Control;
using InferHub.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace InferHub.Node.LocalApi;

/// <summary>
/// <c>/api/control/*</c> — the node's control API (phase 112): what it is doing, its log, its settings, a restart,
/// its engines and models, and its sharing (113). The desktop app (115) is its client; so is <c>curl</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never anonymous, loopback by default (112 D1).</b> Every route needs the control token, on loopback too, and a
/// caller from another machine is a 403 unless <c>Control:AllowRemote</c>. The local API's own keys do not open it:
/// a key that may spend GPU time is not a key that may rewrite the configuration.
/// </para>
/// <para>
/// <b>Nothing here routes, queues or admits</b> (37 D2): every action reaches the same object the hub's commands
/// reach — <see cref="ModelCommandExecutor"/>, <see cref="IEngineControl"/>, the catalogues — one implementation,
/// two callers (112 D6).
/// </para>
/// </remarks>
internal static class LocalControlEndpoints
{
    public const string Prefix = "/api/control";

    private static readonly DateTimeOffset StartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static IEndpointRouteBuilder MapLocalControlEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Prefix).AddEndpointFilter(Authorize);

        group.MapGet("/status", StatusAsync);
        group.MapGet("/activity", (NodeActivity activity) => Results.Json(new { running = activity.Running, recent = activity.Recent }, Json));
        group.MapGet("/events", EventsAsync);
        group.MapGet("/logs", (NodeLogRing ring, long? after, int? max) => Results.Json(new { lines = ring.After(after ?? 0, max ?? 500) }, Json));
        group.MapGet("/logs/stream", LogStreamAsync);
        group.MapGet("/settings", Settings);
        group.MapPut("/settings", SaveSettingsAsync);
        group.MapGet("/settings/file", (LocalSettingsFile local) => Results.Json(Masked(NodeLocalSettings.Read(local.Path)), Json));
        group.MapPut("/settings/file", SaveFileAsync);
        group.MapPut("/sharing", SetSharingAsync);
        group.MapPost("/restart", Restart);
        group.MapGet("/models", ModelsAsync);
        group.MapPost("/models/{name}/{action}", ModelCommandAsync);
        group.MapDelete("/models/{name}", (HttpContext context, string name, string? engine, string? tool) =>
            StartModelCommand(context, ModelCommand.KindDelete, name, engine, tool));
        group.MapPost("/engines/{name}/{action}", EngineAsync);
        group.MapPost("/catalog/{engine}/{model}/{action}", CatalogAsync);

        return app;
    }

    /// <summary>112 D1: the token, always; a remote caller only when the operator said so.</summary>
    private static async ValueTask<object?> Authorize(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var options = context.RequestServices.GetRequiredService<IOptions<ControlOptions>>().Value;
        var remote = context.Connection.RemoteIpAddress;

        if (!options.AllowRemote && remote is not null && !IPAddress.IsLoopback(remote))
        {
            return Error(403, "the control API answers this machine only; set Control:AllowRemote=true to allow another (the token is still required)");
        }

        var header = context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        var token = context.RequestServices.GetRequiredService<ControlToken>();

        if (!token.Matches(presented))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(401, presented is null
                ? $"the control API needs its token: Authorization: Bearer <token> ({(token.Source == "file" ? $"in {token.FilePath}" : "Control:Token")})"
                : "that is not this node's control token");
        }

        return await next(invocation);
    }

    private static async Task<IResult> StatusAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        var nodeOptions = services.GetRequiredService<IOptionsMonitor<NodeOptions>>().CurrentValue;
        var nodeId = services.GetRequiredService<INodeIdentity>().GetOrCreateNodeId();
        var backend = services.GetRequiredService<IInferenceBackend>();
        var connection = services.GetRequiredService<CoordinatorConnection>();
        var activity = services.GetRequiredService<NodeActivity>();
        var sharing = services.GetRequiredService<NodeSharing>();
        var restarter = services.GetRequiredService<INodeRestarter>();
        var localApi = services.GetRequiredService<IOptions<LocalApiOptions>>().Value;
        var retrieval = services.GetRequiredService<IOptions<LocalRetrievalOptions>>().Value;

        IReadOnlyList<ModelInfo> models;

        try
        {
            models = await backend.ListModelsAsync(cancellationToken) ?? [];
        }
        catch (Exception)
        {
            models = [];
        }

        var sharingState = sharing.State(nodeId);

        return Results.Json(new
        {
            node = new
            {
                id = nodeId,
                name = nodeOptions.Name,
                version = Version(),
                startedAtUtc = StartedAt,
                uptimeSeconds = Math.Round((DateTimeOffset.UtcNow - StartedAt).TotalSeconds),
                localSettingsFile = services.GetRequiredService<LocalSettingsFile>().Path,
                maxConcurrency = nodeOptions.MaxConcurrency,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            },
            network = new
            {
                state = connection.ConnectionState,
                coordinator = connection.CoordinatorUrl,
                hubHoldsSharing = connection.HubHoldsSharing
            },
            sharing = new
            {
                sharingState.Mode,
                sharingState.Paused,
                sharingState.Reason,
                sharingState.SinceUtc,
                idleSeconds = nodeOptions.Sharing.IdleSeconds,
                // Phase 116: the owner's terms, and how much of today's allowance the network has had.
                sharingState.ResumesAtUtc,
                hours = nodeOptions.Sharing.Hours,
                timeZone = nodeOptions.Sharing.TimeZone,
                zone = sharing.Hours.Zone.Id,
                allowance = sharing.Allowance(),
                // Phase 117: which models the network may use; each model below says whether it is one of them.
                models = new { include = nodeOptions.Sharing.Models.Include, exclude = nodeOptions.Sharing.Models.Exclude },
                // 113 D1: a pause an old hub cannot hold is said, never shown as held.
                held = connection.ConnectionState == "disabled" ? (bool?)null : connection.HubHoldsSharing
            },
            jobs = new
            {
                running = activity.Running.Count,
                local = activity.RunningCount(NodeActivity.Local),
                network = activity.RunningCount(NodeActivity.Mesh)
            },
            local = new { urls = localApi.Urls, retrieval = new { enabled = retrieval.Enabled, @private = retrieval.Private } },
            backend = new { name = backend.Name, endpoint = backend.Endpoint, kinds = backend.Kinds },
            engines = services.GetService<IEngineControl>()?.State(nodeId).Engines,
            colibri = services.GetService<Backends.Colibri.IColibriControl>()?.State(nodeId),
            strata = services.GetService<Backends.Strata.IStrataControl>()?.State(nodeId),
            models = models.Select(m => new { name = m.Name, size = m.SizeBytes, shared = sharing.SharedState(m.Name) }),
            limits = services.GetService<Resources.Limits.IEngineLimiter>()?.State(nodeId),
            update = services.GetService<Update.UpdateManager>()?.State(nodeId),
            restart = restarter.Support
        }, Json);
    }

    private static async Task EventsAsync(HttpContext context, NodeActivity activity, long? after, CancellationToken requestAborted)
    {
        // A stream ends with the node, not only with the client: held open, it made a restart wait out the host's
        // shutdown timeout (found live, 79 s) and end in an exception instead of the restart exit code.
        using var stopping = Stopping(context, requestAborted);
        var cancellationToken = stopping.Token;
        StartEventStream(context);
        var last = after ?? 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var item in activity.EventsAfter(last))
            {
                await WriteEventAsync(context, item.Seq, item.Type, item, cancellationToken);
                last = item.Seq;
            }

            if (!await WaitOrKeepAliveAsync(context, ct => activity.WaitAsync(last, ct), cancellationToken))
            {
                return;
            }
        }
    }

    private static async Task LogStreamAsync(HttpContext context, NodeLogRing ring, long? after, CancellationToken requestAborted)
    {
        using var stopping = Stopping(context, requestAborted);
        var cancellationToken = stopping.Token;
        StartEventStream(context);
        var last = after ?? 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (var line in ring.After(last, 5000))
            {
                await WriteEventAsync(context, line.Seq, "log", line, cancellationToken);
                last = line.Seq;
            }

            if (!await WaitOrKeepAliveAsync(context, ct => ring.WaitAsync(last, ct), cancellationToken))
            {
                return;
            }
        }
    }

    private static IResult Settings(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var path = context.RequestServices.GetRequiredService<LocalSettingsFile>().Path;
        var file = NodeLocalSettings.Read(path);

        return Results.Json(new
        {
            file = path,
            keys = NodeSettingsSchema.Keys.Select(key => new
            {
                key.Key,
                key.Type,
                key.Live,
                key.Group,
                key.About,
                key.Values,
                value = NodeSettingsSchema.Show(configuration, key),
                inFile = InFile(file, key.Key)
            })
        }, Json);
    }

    private sealed record SettingsChange(Dictionary<string, JsonNode?>? Set, List<string>? Remove);

    private static async Task<IResult> SaveSettingsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        SettingsChange? change;

        try
        {
            change = await JsonSerializer.DeserializeAsync<SettingsChange>(context.Request.Body, Json, cancellationToken);
        }
        catch (JsonException ex)
        {
            return Error(400, $"the body must be {{\"set\": {{key: value}}, \"remove\": [key]}}: {ex.Message}");
        }

        if (change is null || (change.Set is not { Count: > 0 } && change.Remove is not { Count: > 0 }))
        {
            return Error(400, "nothing to change: send {\"set\": {key: value}} and/or {\"remove\": [key]}");
        }

        var file = NodeLocalSettings.Read(context.RequestServices.GetRequiredService<LocalSettingsFile>().Path);
        var touched = new List<SettingKey>();

        foreach (var (name, value) in change.Set ?? [])
        {
            if (NodeSettingsSchema.Find(name) is not { } key)
            {
                return Error(400, $"'{name}' is not a setting this API writes; GET {Prefix}/settings lists them, PUT {Prefix}/settings/file writes the whole file");
            }

            var coerced = NodeSettingsSchema.Coerce(key, value, out var problem);

            if (problem is not null)
            {
                return Error(400, problem);
            }

            NodeLocalSettings.Set(file, key.Key, coerced);
            touched.Add(key);
        }

        foreach (var name in change.Remove ?? [])
        {
            if (NodeSettingsSchema.Find(name) is not { } key)
            {
                return Error(400, $"'{name}' is not a setting this API writes");
            }

            NodeLocalSettings.Remove(file, key.Key);
            touched.Add(key);
        }

        return Persist(context, file, touched);
    }

    private static async Task<IResult> SaveFileAsync(HttpContext context, CancellationToken cancellationToken)
    {
        JsonObject? file;

        try
        {
            file = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken) as JsonObject;
        }
        catch (JsonException ex)
        {
            return Error(400, $"the body must be a JSON object: {ex.Message}");
        }

        if (file is null)
        {
            return Error(400, "the body must be a JSON object");
        }

        // A masked secret sent back unchanged keeps the value the file has.
        RestoreMaskedSecrets(file, NodeLocalSettings.Read(context.RequestServices.GetRequiredService<LocalSettingsFile>().Path));
        return Persist(context, file, null);
    }

    private sealed record SharingChange(string? Mode, int? IdleSeconds, string[]? Hours, string? TimeZone, int? DailyNetworkMinutes, SharedModelsChange? Models);

    /// <summary>Phase 117: either list may be left out (kept) or sent empty (cleared).</summary>
    private sealed record SharedModelsChange(string[]? Include, string[]? Exclude);

    /// <summary>
    /// 113's mode, and since 116 the owner's terms: any of <c>mode</c>, <c>idleSeconds</c>, <c>hours</c> (an array of
    /// lines, <c>[]</c> clears them), <c>timeZone</c> (<c>""</c> clears it) and <c>dailyNetworkMinutes</c>; since 117
    /// <c>models</c> (<c>{include, exclude}</c>, each an array, <c>[]</c> clears it). What is left out keeps its value.
    /// </summary>
    private static async Task<IResult> SetSharingAsync(HttpContext context, CancellationToken cancellationToken)
    {
        SharingChange? change;

        try
        {
            change = await JsonSerializer.DeserializeAsync<SharingChange>(context.Request.Body, Json, cancellationToken);
        }
        catch (JsonException)
        {
            change = null;
        }

        if (change is null || change is { Mode: null, IdleSeconds: null, Hours: null, TimeZone: null, DailyNetworkMinutes: null, Models: null })
        {
            return Error(400, "send at least one of mode, idleSeconds, hours, timeZone, dailyNetworkMinutes, models");
        }

        if (change.Models is { Include: null, Exclude: null })
        {
            return Error(400, "models takes include, exclude or both (each an array of model names; [] clears it)");
        }

        var mode = change.Mode?.Trim().ToLowerInvariant();

        if (change.Mode is not null && (mode is null || !SharingOptions.Modes.Contains(mode)))
        {
            return Error(400, $"mode must be one of {string.Join(", ", SharingOptions.Modes)}");
        }

        var file = NodeLocalSettings.Read(context.RequestServices.GetRequiredService<LocalSettingsFile>().Path);
        var touched = new List<SettingKey>();

        void Write(string key, JsonNode? value)
        {
            if (value is null)
            {
                NodeLocalSettings.Remove(file, key);
            }
            else
            {
                NodeLocalSettings.Set(file, key, value);
            }

            touched.Add(NodeSettingsSchema.Find(key)!);
        }

        if (mode is not null)
        {
            Write("Node:Sharing:Mode", JsonValue.Create(mode));
        }

        if (change.IdleSeconds is { } idle)
        {
            Write("Node:Sharing:IdleSeconds", JsonValue.Create(Math.Max(0, idle)));
        }

        if (change.Hours is { } hours)
        {
            var lines = hours.Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim()).ToArray();
            Write("Node:Sharing:Hours", lines.Length == 0 ? null : new JsonArray([.. lines.Select(l => (JsonNode?)JsonValue.Create(l))]));
        }

        if (change.TimeZone is { } zone)
        {
            Write("Node:Sharing:TimeZone", string.IsNullOrWhiteSpace(zone) ? null : JsonValue.Create(zone.Trim()));
        }

        if (change.DailyNetworkMinutes is { } minutes)
        {
            Write("Node:Sharing:DailyNetworkMinutes", minutes == 0 ? null : JsonValue.Create(minutes));
        }

        if (change.Models is { } shared)
        {
            // One key, Node:Sharing:Models, holding both lists — a list left out is the one the file already has.
            var current = context.RequestServices.GetRequiredService<IOptionsMonitor<NodeOptions>>().CurrentValue.Sharing.Models;
            var include = Names(shared.Include ?? [.. current.Include]);
            var exclude = Names(shared.Exclude ?? [.. current.Exclude]);
            var value = new JsonObject();

            if (include.Length > 0)
            {
                value["Include"] = new JsonArray([.. include.Select(n => (JsonNode?)JsonValue.Create(n))]);
            }

            if (exclude.Length > 0)
            {
                value["Exclude"] = new JsonArray([.. exclude.Select(n => (JsonNode?)JsonValue.Create(n))]);
            }

            Write("Node:Sharing:Models", value.Count == 0 ? null : value);
        }

        return Persist(context, file, touched);

        static string[] Names(IEnumerable<string?> names) =>
            names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>112 D2: validated against the node's own validators before a byte reaches the file.</summary>
    private static IResult Persist(HttpContext context, JsonObject file, IReadOnlyList<SettingKey>? touched)
    {
        var services = context.RequestServices;
        var path = services.GetRequiredService<LocalSettingsFile>().Path;
        using var merged = NodeSettingsSchema.WithLocalFile(services.GetRequiredService<IConfiguration>(), file, path);
        var failures = NodeSettingsSchema.Validate(services, merged);

        if (failures.Count > 0)
        {
            return Results.Json(new { error = new { message = "the change was not saved: the node would refuse it", failures } }, Json, statusCode: 400);
        }

        try
        {
            NodeLocalSettings.Write(file, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error(500, $"could not write {path}: {ex.Message}");
        }

        var restart = touched is null
            ? ["(the whole file)"]
            : touched.Where(k => !k.Live).Select(k => k.Key).Distinct().ToList();

        services.GetRequiredService<NodeActivity>().Publish("settings.saved", new { keys = touched?.Select(k => k.Key), restartRequired = restart.Count > 0 });

        return Results.Json(new
        {
            saved = true,
            file = path,
            restartRequired = restart.Count > 0,
            restartFor = restart
        }, Json);
    }

    private static IResult Restart(HttpContext context, bool? force)
    {
        var restarter = context.RequestServices.GetRequiredService<INodeRestarter>();
        var support = restarter.Support;

        if (!support.Supported && force is not true)
        {
            return Error(409, $"not restarting: {support.Why}. POST {Prefix}/restart?force=true ends the node anyway");
        }

        context.RequestServices.GetRequiredService<NodeActivity>().Publish("node.restarting", support);
        restarter.Restart("asked through the control API");
        return Results.Json(new { restarting = true, support.Supervisor, support.Why }, Json, statusCode: 202);
    }

    private static async Task<IResult> ModelsAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        var backend = services.GetRequiredService<IInferenceBackend>();
        var nodeId = services.GetRequiredService<INodeIdentity>().GetOrCreateNodeId();
        var sharing = services.GetRequiredService<NodeSharing>();
        IReadOnlyList<ModelInfo> models;

        try
        {
            models = await backend.ListModelsAsync(cancellationToken) ?? [];
        }
        catch (Exception ex)
        {
            return Error(502, $"the engine did not list its models: {ex.Message}");
        }

        return Results.Json(new
        {
            managesModels = services.GetRequiredService<ModelCommandExecutor>().ManagesModels,
            // Phase 117: the lists behind each model's `shared`, so a client can change one model without a status call.
            sharing = new
            {
                include = services.GetRequiredService<IOptionsMonitor<NodeOptions>>().CurrentValue.Sharing.Models.Include,
                exclude = services.GetRequiredService<IOptionsMonitor<NodeOptions>>().CurrentValue.Sharing.Models.Exclude
            },
            models = models.Select(m => new { name = m.Name, size = m.SizeBytes, digest = m.Digest, kinds = (backend as IModelKinds)?.KindsFor(m.Name), shared = sharing.SharedState(m.Name) }),
            colibri = services.GetService<Backends.Colibri.IColibriControl>()?.State(nodeId),
            strata = services.GetService<Backends.Strata.IStrataControl>()?.State(nodeId)
        }, Json);
    }

    private static IResult ModelCommandAsync(HttpContext context, string name, string action, string? engine, string? tool)
    {
        var kind = action.ToLowerInvariant() switch
        {
            "pull" => ModelCommand.KindPull,
            "warm" => ModelCommand.KindWarm,
            "unload" => ModelCommand.KindUnload,
            _ => null
        };

        return kind is null
            ? Error(404, $"'{action}' is not a model action: pull, warm or unload (DELETE removes)")
            : StartModelCommand(context, kind, name, engine, tool);
    }

    /// <summary>112 D6: the hub's model command, run here; its progress goes to the event stream.</summary>
    private static IResult StartModelCommand(HttpContext context, string kind, string name, string? engine, string? tool)
    {
        var services = context.RequestServices;
        var executor = services.GetRequiredService<ModelCommandExecutor>();
        var activity = services.GetRequiredService<NodeActivity>();
        var nodeId = services.GetRequiredService<INodeIdentity>().GetOrCreateNodeId();
        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("InferHub.Node.Control");
        var command = new ModelCommand(Guid.NewGuid(), kind, Uri.UnescapeDataString(name), Tool: tool, Engine: engine);

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var progress in executor.ExecuteAsync(command, nodeId, lifetime.ApplicationStopping))
                {
                    activity.Publish("model.progress", progress);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model command {Kind} {Model} from the control API failed", command.Kind, command.ModelName);
                activity.Publish("model.progress", new ModelCommandProgress(command.CommandId, nodeId, command.Kind, command.ModelName, "failed", null, true, ex.Message));
            }
        });

        return Results.Json(new { commandId = command.CommandId, command.Kind, model = command.ModelName, events = $"{Prefix}/events" }, Json, statusCode: 202);
    }

    private static async Task<IResult> EngineAsync(HttpContext context, string name, string action, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;

        if (services.GetService<IEngineControl>() is not { } engines)
        {
            return Error(404, "this node runs one backend, not Backend:Engines; there is nothing to start or stop");
        }

        if (!engines.EngineNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return Error(404, $"no engine '{name}' here; this node has {string.Join(", ", engines.EngineNames)}");
        }

        bool run = action.ToLowerInvariant() switch { "start" => true, "stop" => false, _ => throw new BadHttpRequestException("start or stop") };
        var nodeId = services.GetRequiredService<INodeIdentity>().GetOrCreateNodeId();
        var desired = engines.State(nodeId).Engines.ToDictionary(
            e => e.Name,
            e => e.State is NodeEngineInfo.Running or NodeEngineInfo.Starting,
            StringComparer.OrdinalIgnoreCase);
        desired[name] = run;

        var refusals = await engines.ApplyAsync(desired, cancellationToken);
        return Results.Json(new { engine = name, running = run, refusals, note = HubNote(services) }, Json);
    }

    private static async Task<IResult> CatalogAsync(HttpContext context, string engine, string model, string action, CancellationToken cancellationToken)
    {
        var services = context.RequestServices;
        Backends.Catalog.ICatalogControl? catalog = engine.ToLowerInvariant() switch
        {
            "colibri" => services.GetService<Backends.Colibri.IColibriControl>(),
            "strata" => services.GetService<Backends.Strata.IStrataControl>(),
            _ => null
        };

        if (catalog is null)
        {
            return Error(404, $"this node has no '{engine}' catalogue (colibri or strata)");
        }

        var nodeId = services.GetRequiredService<INodeIdentity>().GetOrCreateNodeId();
        var loaded = catalog.State(nodeId).Models
            .Where(m => m.Pinned || m.State == "loaded")
            .Select(m => m.Name)
            .ToList();
        model = Uri.UnescapeDataString(model);

        switch (action.ToLowerInvariant())
        {
            case "load":
                if (!loaded.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    loaded.Add(model);
                }

                break;

            case "unload":
                loaded.RemoveAll(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
                break;

            default:
                return Error(404, $"'{action}' is not a catalogue action: load or unload");
        }

        var refusals = await catalog.ApplyAsync(new CatalogProfile(Loaded: loaded), cancellationToken);
        return Results.Json(new { engine, model, action, refusals, note = HubNote(services) }, Json);
    }

    /// <summary>112 D6: on a meshed node the hub's profile may set this again; said, not hidden.</summary>
    private static string? HubNote(IServiceProvider services) =>
        services.GetRequiredService<CoordinatorConnection>().ConnectionState is "connected"
            ? "this node is in a network: a coordinator's profile for it may change this again"
            : null;

    private static CancellationTokenSource Stopping(HttpContext context, CancellationToken requestAborted) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            requestAborted,
            context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

    private static void StartEventStream(HttpContext context)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteEventAsync(HttpContext context, long id, string type, object data, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync($"id: {id}\nevent: {type}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Waits for the next item, writing an SSE comment every 15 s so proxies keep the stream; false when the client left.</summary>
    private static async Task<bool> WaitOrKeepAliveAsync(HttpContext context, Func<CancellationToken, Task> wait, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await wait(timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await context.Response.WriteAsync(": keep-alive\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool InFile(JsonObject file, string key)
    {
        JsonNode? current = file;

        foreach (var part in key.Split(':'))
        {
            if (current is not JsonObject obj)
            {
                return false;
            }

            current = obj.FirstOrDefault(p => string.Equals(p.Key, part, StringComparison.OrdinalIgnoreCase)).Value;

            if (current is null)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonObject Masked(JsonObject file)
    {
        var copy = (JsonObject)file.DeepClone();
        Mask(copy, string.Empty);
        return copy;
    }

    private static void Mask(JsonObject node, string path)
    {
        foreach (var name in node.Select(p => p.Key).ToList())
        {
            var key = path.Length == 0 ? name : $"{path}:{name}";

            if (node[name] is JsonObject child)
            {
                Mask(child, key);
            }
            else if (node[name] is not null && NodeSettingsSchema.IsSecret(key))
            {
                node[name] = "(set)";
            }
        }
    }

    private static void RestoreMaskedSecrets(JsonObject incoming, JsonObject existing)
    {
        foreach (var name in incoming.Select(p => p.Key).ToList())
        {
            var match = existing.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

            if (incoming[name] is JsonObject child && match is JsonObject existingChild)
            {
                RestoreMaskedSecrets(child, existingChild);
            }
            else if (incoming[name] is JsonValue value && value.TryGetValue<string>(out var text) && text == "(set)" && match is not null)
            {
                incoming[name] = match.DeepClone();
            }
        }
    }

    private static string Version() =>
        typeof(LocalControlEndpoints).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private static IResult Error(int status, string message) =>
        Results.Json(new { error = new { message } }, Json, statusCode: status);
}
