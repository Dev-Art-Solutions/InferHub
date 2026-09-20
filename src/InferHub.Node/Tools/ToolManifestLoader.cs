using System.Runtime.InteropServices;
using System.Text.Json;
using InferHub.Shared.Contracts;

namespace InferHub.Node.Tools;

/// <summary>
/// Reads <c>*.json</c> manifests out of <c>Tools:ManifestDirectory</c>.
/// </summary>
/// <remarks>
/// <b>A load error is logged and skips that tool; it never fails the host.</b> One malformed
/// manifest must not take a node's inference offline — the box still has a GPU and a backend, and
/// a fleet that loses a chat node because somebody fat-fingered a JSON comma has traded a small
/// problem for a large one. The refusal is loud in the log and the capability simply never appears,
/// which is the same signal a missing model gives.
/// </remarks>
public static class ToolManifestLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<ToolManifest> LoadDirectory(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            logger.LogInformation(
                "Tools are on but no manifest directory exists at {Directory}; nothing to load.",
                directory);

            return Array.Empty<ToolManifest>();
        }

        var manifests = new List<ToolManifest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string text;

            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not read tool manifest {Path}; skipping it.", path);
                continue;
            }

            if (!TryParse(text, path, out var manifest, out var error))
            {
                logger.LogError("Tool manifest {Path} is not usable: {Error} Skipping it.", path, error);
                continue;
            }

            if (!seen.Add(manifest!.Id))
            {
                logger.LogError(
                    "Tool manifest {Path} declares id '{Id}', which another manifest in {Directory} already used. Skipping it — two tools under one id would make Tools:Allowed ambiguous.",
                    path,
                    manifest.Id,
                    directory);

                continue;
            }

            manifests.Add(manifest);
        }

        return manifests;
    }

    /// <summary>Parses one manifest. Internal so the tests can drive the refusals without a directory.</summary>
    internal static bool TryParse(string text, string path, out ToolManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;

        ToolManifestFile? file;

        try
        {
            file = JsonSerializer.Deserialize<ToolManifestFile>(text, Json);
        }
        catch (JsonException ex)
        {
            error = $"it is not valid JSON ({ex.Message}).";
            return false;
        }

        if (file is null)
        {
            error = "it is empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(file.Id))
        {
            error = "'id' is required — it is what Tools:Allowed names.";
            return false;
        }

        if (file.Command is null)
        {
            error = "'command' is required and must be an argv array, e.g. [\"/usr/bin/python3\", \"-u\", \"worker.py\"], " +
                "or an object keyed by platform ({\"linux\": [...], \"windows\": [...]}) when the argv differs by OS.";

            return false;
        }

        if (!TryResolveCommand(file.Command.Value, out var command, out var commandError))
        {
            error = commandError;
            return false;
        }

        // `models: []` is a *deliberate* open set (phase 42) and `models` omitted is a mistake, so
        // the two are distinguished by null-vs-empty rather than collapsed. An open set means "ask
        // the worker which ones it found" — the TTS worker's models are voice files an operator
        // dropped into a directory, and no list written in advance survives the first new voice.
        // The kind is still the ceiling: see ToolWorkerPool.Narrow.
        var capabilities = (file.Capabilities ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.Kind) && c.Models is not null)
            .Select(c => new NodeCapability(
                c.Kind!.Trim(),
                c.Models!
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .Select(m => m.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();

        if (capabilities.Length == 0)
        {
            error = "'capabilities' must declare at least one { kind, models } pair — a tool that claims nothing can never be routed to. Use \"models\": [] to let the worker report what it found.";
            return false;
        }

        if (file.MaxWorkers < 1)
        {
            error = $"'maxWorkers' must be >= 1 (got {file.MaxWorkers}).";
            return false;
        }

        if (file.MinWorkers < 0 || file.MinWorkers > file.MaxWorkers)
        {
            error = $"'minWorkers' must be between 0 and maxWorkers (got {file.MinWorkers}, maxWorkers {file.MaxWorkers}).";
            return false;
        }

        foreach (var (name, seconds) in new[]
                 {
                     ("startTimeoutSeconds", file.StartTimeoutSeconds),
                     ("requestTimeoutSeconds", file.RequestTimeoutSeconds),
                     ("idleTimeoutSeconds", file.IdleTimeoutSeconds)
                 })
        {
            if (seconds < 1)
            {
                error = $"'{name}' must be >= 1 (got {seconds}).";
                return false;
            }
        }

        if (!TryResolveWorkdir(file.Workdir, out var workdir, out var workdirError))
        {
            error = workdirError;
            return false;
        }

        manifest = new ToolManifest
        {
            Id = file.Id!.Trim(),
            Capabilities = capabilities,
            Command = command,
            WorkingDirectory = workdir,
            Environment = file.Env is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(file.Env, StringComparer.Ordinal),
            MinWorkers = file.MinWorkers,
            MaxWorkers = file.MaxWorkers,
            StartTimeoutSeconds = file.StartTimeoutSeconds,
            RequestTimeoutSeconds = file.RequestTimeoutSeconds,
            IdleTimeoutSeconds = file.IdleTimeoutSeconds
        };

        return true;
    }

    /// <summary>
    /// Phase 79. The name this process resolves a per-platform manifest branch against — the same
    /// three buckets <c>ToolWorkerProcess.ApplyEnvironment</c> already switches on to decide what a
    /// child needs to start at all.
    /// </summary>
    private static string CurrentPlatformKey =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
        : "linux";

    /// <summary>
    /// <c>command</c> is either the argv array every manifest has always written, or (phase 79) an
    /// object keyed by platform — <c>{"linux": [...], "windows": [...]}</c> — for the one field a
    /// worker cannot avoid differing on: the interpreter's own path. A manifest that names only
    /// platforms it does not run on is refused **by name**, the same "loaded, logged, skipped" shape
    /// as an unknown id (D2) — a node must not go offline because one tool cannot start here.
    /// </summary>
    private static bool TryResolveCommand(JsonElement element, out string[] command, out string? error)
    {
        command = [];

        // The single-string command is called out by name because it is the mistake everyone makes
        // (every shell, every CI config and every Docker CMD accepts one) and because the failure
        // it would cause — a program name containing spaces, silently resolved wrong — is the class
        // of bug D3 exists to prevent.
        if (element.ValueKind is JsonValueKind.String)
        {
            error = "'command' is a string. It must be an argv array — [\"/usr/bin/python3\", \"-u\", \"worker.py\"] — because a command line assembled by concatenation is one quoting bug away from being an injection point, and there is no shell here to split it.";
            return false;
        }

        if (element.ValueKind is JsonValueKind.Array)
        {
            return TryReadArgv(element, out command, out error);
        }

        if (element.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, CurrentPlatformKey, StringComparison.OrdinalIgnoreCase))
                {
                    return TryReadArgv(property.Value, out command, out error);
                }
            }

            var offered = string.Join(", ", element.EnumerateObject().Select(p => p.Name));

            error = string.IsNullOrEmpty(offered)
                ? $"'command' is an empty object; it must name a '{CurrentPlatformKey}' branch (this node's platform)."
                : $"'command' does not offer a '{CurrentPlatformKey}' branch (this node's platform) — it offers: {offered}. " +
                  "This tool does not run on this platform.";

            return false;
        }

        error = "'command' is required and must be an argv array, e.g. [\"/usr/bin/python3\", \"-u\", \"worker.py\"], " +
            "or an object keyed by platform ({\"linux\": [...], \"windows\": [...]}) when the argv differs by OS.";

        return false;
    }

    private static bool TryReadArgv(JsonElement array, out string[] command, out string? error)
    {
        if (array.ValueKind is not JsonValueKind.Array)
        {
            command = [];
            error = "'command' must be a non-empty array of non-empty strings.";
            return false;
        }

        var parsed = array
            .EnumerateArray()
            .Select(element => element.ValueKind is JsonValueKind.String ? element.GetString() : null)
            .ToArray();

        if (parsed.Length == 0 || parsed.Any(string.IsNullOrWhiteSpace))
        {
            command = [];
            error = "'command' must be a non-empty array of non-empty strings.";
            return false;
        }

        command = parsed!;
        error = null;
        return true;
    }

    /// <summary>
    /// <c>workdir</c> is optional, so unlike <c>command</c> a platform branch this node does not
    /// match is not a refusal — it resolves to "unset", exactly as an absent field always has, and
    /// the worker starts in the node process's own directory.
    /// </summary>
    private static bool TryResolveWorkdir(JsonElement? element, out string? workdir, out string? error)
    {
        workdir = null;
        error = null;

        if (element is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (value.ValueKind is JsonValueKind.String)
        {
            var text = value.GetString();
            workdir = string.IsNullOrWhiteSpace(text) ? null : text;
            return true;
        }

        if (value.ValueKind is JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (string.Equals(property.Name, CurrentPlatformKey, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind is JsonValueKind.String)
                {
                    var text = property.Value.GetString();
                    workdir = string.IsNullOrWhiteSpace(text) ? null : text;
                    return true;
                }
            }

            return true;
        }

        error = "'workdir' must be a string, or an object keyed by platform ({\"linux\": \"...\", \"windows\": \"...\"}).";
        return false;
    }

    /// <summary>
    /// The on-disk shape. <c>Command</c> and <c>Workdir</c> are <see cref="JsonElement"/> rather than
    /// bound types on purpose: binding directly would turn "somebody wrote a string" into a
    /// <c>JsonException</c> about a token type, and the operator would never learn which field.
    /// </summary>
    private sealed record ToolManifestFile
    {
        public string? Id { get; init; }

        public List<ToolManifestCapability>? Capabilities { get; init; }

        public JsonElement? Command { get; init; }

        public JsonElement? Workdir { get; init; }

        public Dictionary<string, string>? Env { get; init; }

        public int MinWorkers { get; init; }

        public int MaxWorkers { get; init; } = 1;

        public int StartTimeoutSeconds { get; init; } = 120;

        public int RequestTimeoutSeconds { get; init; } = 600;

        public int IdleTimeoutSeconds { get; init; } = 900;
    }

    private sealed record ToolManifestCapability
    {
        public string? Kind { get; init; }

        public List<string>? Models { get; init; }
    }
}
