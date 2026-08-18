using System.Globalization;
using InferHub.Node.Configuration;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Tools;

/// <summary>
/// The real runtime: one <see cref="ToolWorkerPool"/> per allowed manifest, plus the maintenance
/// loop that retires idle workers and probes pools that have given up.
/// </summary>
/// <remarks>
/// <b>Nothing is discovered-and-run</b> (D2). Every manifest in the directory is loaded and
/// reported; only the ones named in <c>Tools:Allowed</c> get a pool. A manifest that is present but
/// not allowed is logged once at startup with its id, because "I put the file there and nothing
/// happened" is otherwise a silent afternoon.
/// </remarks>
internal sealed class ProcessToolRuntime : IToolRuntime, IHostedService, IAsyncDisposable
{
    private readonly ToolOptions options;
    private readonly VramOptions vram;
    private readonly TimeProvider time;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<ProcessToolRuntime> logger;
    private readonly List<ToolWorkerPool> pools = new();
    private readonly CancellationTokenSource lifetime = new();

    /// <summary>
    /// The image catalogue, as far as the node is concerned: id, licence, VRAM (phase 48). Loaded
    /// once at startup from <c>Tools:Image:RecipeDirectory</c> — the same files the worker reads,
    /// and only the three fields that are the node's business.
    /// </summary>
    private IReadOnlyDictionary<string, ImageRecipeInfo> recipes =
        new Dictionary<string, ImageRecipeInfo>(StringComparer.OrdinalIgnoreCase);

    private readonly ImageResidency residency;

    /// <summary>Models a coordinator profile narrowed away (phase 48). Never widened here.</summary>
    private volatile IReadOnlyCollection<string> profileDisabledModels = Array.Empty<string>();

    /// <summary>
    /// Manifest ids that were loaded and never started because <c>Tools:Allowed</c> does not name
    /// them (D2). Kept so the hub can be told, rather than only the log on this box.
    /// </summary>
    private readonly List<string> notAllowed = new();

    private Task? maintenance;

    public ProcessToolRuntime(
        IOptions<ToolOptions> toolOptions,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        ILogger<ProcessToolRuntime> logger,
        IOptions<NodeOptions>? nodeOptions = null)
    {
        options = toolOptions.Value;

        // Optional, and defaulted to "no budget declared" — which is v3.15's behaviour exactly. A
        // node whose configuration says nothing about VRAM must behave as it did before this phase
        // existed, and a required dependency here would have made that impossible to express.
        vram = nodeOptions?.Value.Vram ?? new VramOptions { BudgetMiB = 0 };
        this.time = time;
        this.loggerFactory = loggerFactory;
        this.logger = logger;
        residency = new ImageResidency(options.Image.ResidentRecipes);
    }

    public bool Enabled => true;

    public IReadOnlyList<NodeCapability> Capabilities
    {
        get
        {
            // Merged per kind rather than per pool: the node declares "I can transcribe these
            // models", not "tool A can transcribe these and tool B those", because the routing key
            // is (capability, model) and which process serves it is the node's business.
            var merged = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            lock (pools)
            {
                foreach (var capability in pools.SelectMany(pool => pool.Capabilities))
                {
                    if (!merged.TryGetValue(capability.Kind, out var models))
                    {
                        models = new List<string>();
                        merged[capability.Kind] = models;
                    }

                    foreach (var model in capability.Models)
                    {
                        if (!models.Contains(model, StringComparer.OrdinalIgnoreCase))
                        {
                            models.Add(model);
                        }
                    }
                }
            }

            return merged
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new NodeCapability(
                    pair.Key,
                    NarrowImageRecipes(pair.Key, pair.Value)
                        .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .Where(capability => capability.Models.Count > 0)
                .ToArray();
        }
    }

    /// <summary>
    /// Drops image recipes this node will not run: an unaccepted licence, or one that cannot fit in
    /// the declared VRAM budget (phase 48, D2/D5).
    /// </summary>
    /// <remarks>
    /// <b>Not declared, rather than declared-and-refused</b>, which is phase-41 D6's
    /// withdraw-on-failure applied <em>before</em> the first failure: a model the fleet never sees
    /// is a model the router never sends work to, so nobody pays a request to find out. The
    /// alternative — advertise it and answer 503 — spends a routing decision and a client's retry
    /// budget on a fact this node knew at startup.
    /// </remarks>
    private IEnumerable<string> NarrowImageRecipes(string kind, IEnumerable<string> models)
    {
        var disabled = profileDisabledModels;

        if (disabled.Count > 0)
        {
            // A profile's narrowing applies to any kind, not only images: the hub asked this node
            // to stop offering a model and the answer to that is never "which sort of model?".
            models = models.Where(model =>
                !disabled.Any(id => string.Equals(id?.Trim(), model, StringComparison.OrdinalIgnoreCase)));
        }

        // Phase 57: the two image kinds AND video. The licence gate and the budget are about
        // WEIGHTS ON A CARD, and a node that applied them to the image kinds only would happily
        // render video with a model whose licence nobody accepted — 50 D1's sentence, one kind on.
        if (!CapabilityKinds.IsGenerativeMedia(kind)
            || recipes.Count == 0)
        {
            return models;
        }

        return models.Where(model =>
        {
            if (!recipes.TryGetValue(model, out var recipe))
            {
                // A model the worker offers and the node has no recipe file for. Trusted: the
                // worker is the authority on what it can serve, and a node that silently dropped a
                // model because its own view of the directory was stale would be the harder bug.
                return true;
            }

            return recipe.IsLicensed(options.Image.AcceptedLicenses)
                && VramBudget.Fits(vram.BudgetMiB, vram.ReserveMiB, recipe.VramMiB);
        });
    }

    public event Action? CapabilitiesChanged;

    /// <summary>
    /// Phase 45. Every manifest on the box, started or not, so the four ways a tool can fail to serve
    /// — not allowed, suspended by a profile, given up, or simply running — are four different
    /// answers at the hub rather than one absence.
    /// </summary>
    public NodeToolState State(string nodeId)
    {
        ToolWorkerPool[] snapshot;
        string[] refused;

        lock (pools)
        {
            snapshot = pools.ToArray();
            refused = notAllowed.ToArray();
        }

        var tools = snapshot
            .Select(pool => pool.Report())
            .Concat(refused.Select(id => new NodeToolInfo(
                id,
                Allowed: false,
                NodeToolInfo.NotAllowed,
                Array.Empty<NodeCapability>(),
                MaxWorkers: 0,
                Workers: 0,
                Busy: 0,
                Requests: 0,
                Failures: 0,
                LastError: null,
                LastErrorAtUtc: null)))
            .OrderBy(tool => tool.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NodeToolState(
            nodeId,
            Enabled: true,
            tools,
            DateTimeOffset.UtcNow,
            VramState(snapshot),
            ImageRecipeStates(tools));
    }

    /// <summary>
    /// Every recipe in the catalogue and why each one is or is not offered (phase 51, D1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The order of the checks is the order of the fixes</b>, and it is not arbitrary. A recipe
    /// that is both unlicensed and too big for the card reports <c>unlicensed</c>, because reading a
    /// licence is the decision that has to happen first and telling somebody to buy a bigger card
    /// for a model they may not be allowed to run is the wrong advice in the wrong order.
    /// </para>
    /// <para>
    /// <c>not-ready</c> is last and is deliberately a <em>catch-all</em>: weights still fetching, a
    /// fetch that failed, a recipe not marked <c>cpuViable</c> on a CPU-only box, or a pool that is
    /// not running. Splitting those apart here would mean the node inferring a worker's reasons,
    /// and the worker already logs the real one — so this reason's job is to send the operator to
    /// the log rather than to guess on their behalf.
    /// </para>
    /// </remarks>
    private IReadOnlyList<NodeImageRecipeState>? ImageRecipeStates(IReadOnlyList<NodeToolInfo> tools)
    {
        if (recipes.Count == 0)
        {
            // Not an empty list: a node with no image recipes has nothing to say here, and an empty
            // array would put an "Images" heading on the console of every chat-only box.
            return null;
        }

        var offeredKinds = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var capability in tools.SelectMany(tool => tool.Capabilities))
        {
            // Every kind served from a recipe, image and video alike (phase 59, D1). Phase 57 kept
            // video out of here so it could not land in a panel that draws pictures; the console now
            // splits on the recipe's `media` instead, and the cost of the old arrangement — a video
            // recipe refused for its licence or its budget being invisible at the hub — is paid off
            // rather than restated.
            if (!CapabilityKinds.IsGenerativeMedia(capability.Kind))
            {
                continue;
            }

            foreach (var model in capability.Models)
            {
                if (!offeredKinds.TryGetValue(model, out var kinds))
                {
                    offeredKinds[model] = kinds = new List<string>();
                }

                if (!kinds.Contains(capability.Kind, StringComparer.OrdinalIgnoreCase))
                {
                    kinds.Add(capability.Kind);
                }
            }
        }

        var disabled = profileDisabledModels;

        return recipes.Values
            .OrderBy(recipe => recipe.Id, StringComparer.OrdinalIgnoreCase)
            .Select(recipe =>
            {
                var kinds = offeredKinds.TryGetValue(recipe.Id, out var found)
                    ? found.OrderBy(k => k, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();

                var reason = kinds.Length > 0 ? ImageRecipeReasons.Ok
                    : !recipe.IsLicensed(options.Image.AcceptedLicenses) ? ImageRecipeReasons.Unlicensed
                    : !VramBudget.Fits(vram.BudgetMiB, vram.ReserveMiB, recipe.VramMiB) ? ImageRecipeReasons.OverBudget
                    : disabled.Any(id => string.Equals(id?.Trim(), recipe.Id, StringComparison.OrdinalIgnoreCase))
                        ? ImageRecipeReasons.Narrowed
                        : ImageRecipeReasons.NotReady;

                return new NodeImageRecipeState(
                    recipe.Id,
                    kinds.Length > 0,
                    reason,
                    kinds,
                    recipe.VramMiB,
                    recipe.LicenseId,
                    recipe.LicenseUrl,
                    recipe.Quantization,

                    // The fourth field phase 58 taught the node to read, travelling one level up so
                    // the hub can tell a refused clip from a refused picture (59 D1).
                    recipe.Media);
            })
            .ToArray();
    }

    /// <summary>
    /// The card, as this node understands it (phase 48). Null when no budget was declared, because
    /// an undeclared budget is an absence rather than a zero (phase-28 D5).
    /// </summary>
    private NodeVramState? VramState(IReadOnlyCollection<ToolWorkerPool> snapshot)
    {
        if (vram.BudgetMiB <= 0)
        {
            return null;
        }

        var measured = snapshot
            .Select(pool => pool.ReportedVramTotalMiB)
            .FirstOrDefault(value => value is > 0);

        return new NodeVramState(
            vram.BudgetMiB,
            vram.ReserveMiB,
            measured,
            residency.Snapshot()
                .Select(r => new NodeResidentModel(r.Model, r.VramMiB, r.InUse))
                .OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var directory = options.ResolvedManifestDirectory();
        var manifests = ToolManifestLoader.LoadDirectory(directory, logger);

        LoadRecipeCatalogue();

        foreach (var manifest in manifests)
        {
            if (!options.IsAllowed(manifest.Id))
            {
                logger.LogWarning(
                    "Tool manifest '{ToolId}' was loaded from {Directory} but is not in {Key}, so it will not be started. Add its id to that list to run it.",
                    manifest.Id,
                    directory,
                    $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)}");

                lock (pools)
                {
                    notAllowed.Add(manifest.Id);
                }

                continue;
            }

            var pool = new ToolWorkerPool(
                manifest,
                options,
                time,
                loggerFactory.CreateLogger($"InferHub.Node.Tools.{manifest.Id}"),
                vram.BudgetMiB);

            pool.CapabilitiesChanged += RaiseCapabilitiesChanged;

            // The worker has been told to free what it was holding, so the node stops believing it
            // holds it. Only idle entries go — anything a lease still covers is left alone, or the
            // gate would admit a second model onto a card that is busy with the first.
            pool.WentIdle += residency.Clear;

            lock (pools)
            {
                pools.Add(pool);
            }

            await pool.StartAsync(cancellationToken);
        }

        var missing = options.Allowed
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Where(id => manifests.All(m => !string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (missing.Length > 0)
        {
            // Allowing a tool that is not there is how a deployment ends up believing it can
            // transcribe. It is a warning rather than a failure because the ordinary cause is a
            // volume that has not been mounted yet, and a node that refuses to boot over it is
            // worse than one that says so.
            logger.LogWarning(
                "{Key} names {Missing}, but no manifest with that id was found in {Directory}.",
                $"{ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)}",
                string.Join(", ", missing.Select(id => $"'{id.Trim()}'")),
                directory);
        }

        logger.LogInformation(
            "Tool runtime is on: {Started} of {Loaded} manifest(s) started from {Directory}, scratch at {Scratch}. Workers run as this node's user with this node's filesystem — this is process isolation, not a sandbox.",
            pools.Count,
            manifests.Count,
            directory,
            options.ResolvedScratchDirectory());

        maintenance = Task.Run(() => MaintainAsync(lifetime.Token), CancellationToken.None);
        RaiseCapabilitiesChanged();
    }

    /// <summary>
    /// Reads the image recipes and says, once, what this box will and will not run and why.
    /// </summary>
    /// <remarks>
    /// The log lines are the whole point of doing this at startup rather than lazily. "I turned it
    /// on and nothing happened" is the single most confusing state the tools track can produce
    /// (phase-45 D1), and for a recipe the answer is almost always one of two sentences an operator
    /// can act on: you have not accepted this licence, or this model does not fit the budget you
    /// declared.
    /// </remarks>
    private void LoadRecipeCatalogue()
    {
        var directory = options.Image.RecipeDirectory;
        recipes = ImageRecipeCatalogue.LoadDirectory(directory, logger);

        if (recipes.Count == 0)
        {
            return;
        }

        foreach (var recipe in recipes.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!recipe.IsLicensed(options.Image.AcceptedLicenses))
            {
                logger.LogWarning(
                    "Image recipe '{Recipe}' is licensed under '{License}', which is not permissive, and this node has not accepted it — so it is not offered. Read it at {Url} and, if you accept it, add \"{License}\" to {Key}.",
                    recipe.Id,
                    recipe.LicenseId,
                    recipe.LicenseUrl ?? "the model repository",
                    recipe.LicenseId,
                    $"{ToolOptions.SectionName}:Image:{nameof(ImageToolOptions.AcceptedLicenses)}");

                continue;
            }

            if (!VramBudget.Fits(vram.BudgetMiB, vram.ReserveMiB, recipe.VramMiB))
            {
                logger.LogWarning(
                    "Image recipe '{Recipe}' needs {Needs} MiB and this node budgets {Headroom} MiB for models (Node:Vram:BudgetMiB {Budget} minus Node:Vram:ReserveMiB {Reserve}), so it is not offered.",
                    recipe.Id,
                    recipe.VramMiB,
                    vram.HeadroomMiB,
                    vram.BudgetMiB,
                    vram.ReserveMiB);
            }
        }

        logger.LogInformation(
            "Image catalogue: {Count} recipe(s) under {Directory}. VRAM budget {Budget} MiB, reserve {Reserve} MiB, at most {Resident} recipe(s) resident.",
            recipes.Count,
            directory,
            vram.BudgetMiB <= 0 ? "not declared" : vram.BudgetMiB.ToString(CultureInfo.InvariantCulture),
            vram.ReserveMiB,
            options.Image.ResidentRecipes);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await lifetime.CancelAsync();

        if (maintenance is not null)
        {
            try
            {
                await maintenance.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposePoolsAsync();
    }

    public async Task<ToolWorkerLease> AcquireAsync(
        string capability,
        string model,
        CancellationToken cancellationToken)
    {
        // The narrowing the declaration applies, applied again at the door. A pool's own capability
        // list knows nothing about a profile, a licence or a budget, so matching on it alone would
        // let a request in through a gate the node had already closed — and a solo caller names the
        // model directly, with no routing decision in front of it to have been narrowed.
        RefuseIfNarrowed(capability, model);

        ToolWorkerPool? pool;

        lock (pools)
        {
            // Matched against the pool's *live* capabilities, not the manifest's: a pool that has
            // given up must read as "this node does not provide it" rather than as a queue.
            // A suspended pool is excluded from both passes (phase 43): a tool the coordinator
            // switched off must read as "this node does not provide it", which is what it now is.
            pool = pools.FirstOrDefault(p => !p.Suspended && Provides(p.Capabilities, capability, model))
                ?? pools.FirstOrDefault(p => !p.Suspended && p.Manifest.Provides(capability, model));
        }

        if (pool is null)
        {
            throw new ToolNotProvidedException(capability, model);
        }

        var lease = await pool.AcquireAsync(cancellationToken);

        // The budget is consulted AFTER the worker slot is taken, and that ordering is the whole
        // trick: only then is "what is in flight" a fact rather than a guess. With `maxWorkers: 1`
        // holding the slot already means nothing else on this pool is on the card, so the common
        // case never refuses — the gate earns its keep when an operator has raised concurrency or
        // when a second recipe would have to be resident beside a running one.
        if (!CapabilityKinds.IsGenerativeMedia(capability)
            || !recipes.TryGetValue(model, out var recipe))
        {
            return lease;
        }

        var decision = VramBudget.Evaluate(
            vram.BudgetMiB,
            vram.ReserveMiB,
            residency.Snapshot(),
            model,
            recipe.VramMiB);

        if (!decision.IsAdmitted)
        {
            await lease.DisposeAsync();

            logger.LogWarning(
                "Refused an image request for '{Recipe}' on VRAM: {Reason}.",
                model,
                decision.Reason);

            // A 503 + Retry-After, the same status and header as every other limit here — never an
            // out-of-memory error inside somebody's job, which is the failure this gate exists to
            // replace.
            throw new ToolVramExhaustedException(decision.Reason ?? "this node has no VRAM budget left for that model");
        }

        residency.Reserve(model, recipe.VramMiB);
        lease.Released = () => residency.Release(model);

        return lease;
    }

    public IReadOnlyList<string> ToolIds
    {
        get
        {
            lock (pools)
            {
                return pools.Select(pool => pool.Manifest.Id).ToArray();
            }
        }
    }

    public IReadOnlyList<ImageRecipeInfo> ImageRecipes => recipes.Values.ToArray();

    public void SetDisabledModels(IReadOnlyCollection<string> models)
    {
        var next = models.Where(model => !string.IsNullOrWhiteSpace(model)).Select(model => model.Trim()).ToArray();

        if (next.SequenceEqual(profileDisabledModels, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        profileDisabledModels = next;
        RaiseCapabilitiesChanged();
    }

    public async Task<ToolWorkerLease> AcquireToolAsync(string toolId, CancellationToken cancellationToken)
    {
        ToolWorkerPool? pool;

        lock (pools)
        {
            pool = pools.FirstOrDefault(p =>
                string.Equals(p.Manifest.Id, toolId?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (pool is null)
        {
            // The ceiling, unchanged: a tool with no pool is a tool `Tools:Allowed` does not name,
            // and a hub cannot conjure one by asking for a model command against it.
            throw new ToolUnavailableException(
                $"this node has no tool '{toolId}'. {ToolOptions.SectionName}:{nameof(ToolOptions.Allowed)} is the operator's grant and a coordinator cannot add to it.");
        }

        if (pool.Suspended)
        {
            throw new ToolUnavailableException(
                $"tool '{toolId}' is switched off by the coordinator's node profile for this node");
        }

        return await pool.AcquireAsync(cancellationToken);
    }

    /// <summary>
    /// Phase 43. The set has already been through <c>NodeProfileClamp</c>, so it is a subset of
    /// <c>Tools:Allowed</c> by construction — nothing here can start a tool the operator did not
    /// grant, because nothing here can create a pool.
    /// </summary>
    public async Task SetDisabledToolsAsync(
        IReadOnlyCollection<string> toolIds,
        CancellationToken cancellationToken)
    {
        ToolWorkerPool[] snapshot;

        lock (pools)
        {
            snapshot = pools.ToArray();
        }

        foreach (var pool in snapshot)
        {
            var shouldSuspend = toolIds.Any(id =>
                string.Equals(id?.Trim(), pool.Manifest.Id, StringComparison.OrdinalIgnoreCase));

            if (shouldSuspend)
            {
                await pool.SuspendAsync();
            }
            else
            {
                await pool.ResumeAsync(cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Dispose();
        await DisposePoolsAsync();
    }

    /// <summary>
    /// The three narrowings the declaration applies, enforced again on the way in (phase 48).
    /// </summary>
    /// <remarks>
    /// It is deliberately additive to the existing matching rather than a replacement for it: the
    /// manifest fallback is what lets a pool whose worker has not reported yet still take a request,
    /// and removing it would reintroduce the v3.10.0 deadlock in a new place.
    /// </remarks>
    private void RefuseIfNarrowed(string capability, string model)
    {
        if (profileDisabledModels.Any(id => string.Equals(id, model, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ToolUnavailableException(
                $"'{model}' is switched off on this node by the coordinator's node profile");
        }

        if (!CapabilityKinds.IsGenerativeMedia(capability)
            || !recipes.TryGetValue(model, out var recipe))
        {
            return;
        }

        if (!recipe.IsLicensed(options.Image.AcceptedLicenses))
        {
            throw new ToolUnavailableException(
                $"'{model}' is licensed under '{recipe.LicenseId}', which is not permissive, and this node has not accepted it. "
                + $"Read it at {recipe.LicenseUrl ?? "the model repository"} and, if you accept it, add \"{recipe.LicenseId}\" to "
                + $"{ToolOptions.SectionName}:Image:{nameof(ImageToolOptions.AcceptedLicenses)}.");
        }

        if (!VramBudget.Fits(vram.BudgetMiB, vram.ReserveMiB, recipe.VramMiB))
        {
            throw new ToolUnavailableException(
                $"'{model}' needs {recipe.VramMiB} MiB and this node budgets {vram.HeadroomMiB} MiB for models "
                + $"(Node:Vram:BudgetMiB {vram.BudgetMiB} minus Node:Vram:ReserveMiB {vram.ReserveMiB})");
        }
    }

    private static bool Provides(IReadOnlyList<NodeCapability> capabilities, string capability, string model) =>
        capabilities.Any(c =>
            string.Equals(c.Kind, capability, StringComparison.OrdinalIgnoreCase)
            && c.Models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase)));

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.MaintenanceInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    return;
                }

                ToolWorkerPool[] snapshot;

                lock (pools)
                {
                    snapshot = pools.ToArray();
                }

                foreach (var pool in snapshot)
                {
                    await pool.MaintainAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tool maintenance pass failed");
            }
        }
    }

    private async Task DisposePoolsAsync()
    {
        ToolWorkerPool[] snapshot;

        lock (pools)
        {
            snapshot = pools.ToArray();
            pools.Clear();
        }

        foreach (var pool in snapshot)
        {
            pool.CapabilitiesChanged -= RaiseCapabilitiesChanged;
            pool.WentIdle -= residency.Clear;
            await pool.DisposeAsync();
        }
    }

    private void RaiseCapabilitiesChanged()
    {
        try
        {
            CapabilitiesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A tool capability subscriber threw");
        }
    }
}
