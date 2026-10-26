using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Control;

/// <summary>One key the control API may write (112 D2).</summary>
/// <param name="Type"><c>string</c>, <c>int</c>, <c>bool</c>, <c>enum</c>, <c>secret</c> (write-only) or <c>json</c> (an object or array).</param>
/// <param name="Live">Read through <c>IOptionsMonitor</c>: in force within a poll. Otherwise a restart applies it.</param>
public sealed record SettingKey(string Key, string Type, bool Live, string Group, string About, IReadOnlyList<string>? Values = null);

/// <summary>
/// The keys the control API writes, and the check a change must pass before it reaches <c>node.local.json</c>
/// (112 D2). A list on purpose: a key that is not on it is a 400 naming it — never a value written that nothing reads.
/// </summary>
public static class NodeSettingsSchema
{
    public static readonly IReadOnlyList<SettingKey> Keys =
    [
        new("Node:Name", "string", false, "node", "The name the hub and the app show."),
        new("Node:MaxConcurrency", "int", false, "node", "Jobs at once, local and network together."),
        new("Node:Sharing:Mode", "enum", true, "sharing", "always: share with the network; idle: pause while this machine uses the node; paused: take no network jobs.", ["always", "idle", "paused"]),
        new("Node:Sharing:IdleSeconds", "int", true, "sharing", "In idle mode, how long after the last local job sharing resumes."),
        new("Node:Sharing:Hours", "json", true, "sharing", "When this node shares: lines like \"mon-fri 18:00-08:00\" or \"weekends all day\" (an array). Empty: any time."),
        new("Node:Sharing:TimeZone", "string", true, "sharing", "The zone the hours and the day are in, e.g. Europe/Sofia. Empty: this machine's."),
        new("Node:Sharing:DailyNetworkMinutes", "int", true, "sharing", "Minutes of network jobs a day, reset at midnight. 0: no allowance."),
        new("Node:Sharing:Models", "json", true, "sharing", "Which models the network may use: {\"Include\": [only these], \"Exclude\": [all but these]}. Empty: every model. A model left out is still this machine's."),
        new("Node:ResourceLimits:Engines", "json", true, "limits", "Hard limits per runtime: {ollama|llamacpp|colibri|strata|tools: {CpuPercent, Cores, MemoryMiB, VramMiB, Gpus}}."),
        new("Coordinator:Enabled", "bool", false, "network", "Join a network: connect to a coordinator."),
        new("Coordinator:Url", "string", false, "network", "The coordinator's address, e.g. https://hub.example.com/."),
        new("Coordinator:EnrollmentSecret", "secret", false, "network", "The secret the coordinator expects from its nodes."),
        new("LocalApi:Urls", "string", false, "local", "Where this node's own API listens, e.g. http://127.0.0.1:5081."),
        new("LocalApi:ApiKeys", "json", false, "local", "Keys a client of the local API must send (an array). Not shown once set.", null),
        new("LocalApi:Retrieval:Enabled", "bool", false, "local", "RAG over a corpus on this node."),
        new("LocalApi:Retrieval:Private", "bool", false, "local", "Keep that corpus private while in a network (phase 114)."),
        new("LocalApi:Retrieval:DataDirectory", "string", false, "local", "Where the private corpus lives."),
        new("Ollama:Endpoint", "string", false, "engines", "The Ollama this node drives."),
        new("Backend:Type", "string", false, "engines", "One backend: ollama, llamacpp, colibri, strata, openai… Empty when Backend:Engines is used."),
        new("Backend:Engines", "json", false, "engines", "Several engines on one node: {name: {Type, Autostart, Serve…}}."),
        new("Colibri:Serve:Vulkan", "bool", false, "engines", "colibri on the card through Vulkan: the dense layers and the hot experts on the GPU (phase 118). Empty: as the environment says."),
        new("Colibri:Serve:ModelsDir", "string", false, "engines", "A folder of converted colibri models."),
        new("Colibri:Serve:Python", "string", false, "engines", "The Python that runs colibri's launcher."),
        new("Colibri:Serve:Launcher", "string", false, "engines", "colibri's coli launcher."),
        new("Colibri:Serve:MaxLoaded", "int", false, "engines", "colibri models loaded at once."),
        new("Colibri:Serve:OnDemand", "bool", false, "engines", "Free an idle colibri model's memory."),
        new("Colibri:KvSlots", "int", false, "engines", "Conversations a colibri engine runs at once (1–16)."),
        new("Strata:Root", "string", false, "engines", "A Strata install."),
        new("Strata:Serve:MaxLoaded", "int", false, "engines", "Strata sizes loaded at once."),
        new("Strata:Serve:OnDemand", "bool", false, "engines", "Free an idle Strata size's memory."),
        new("Upstream:TimeoutSeconds", "int", false, "engines", "How long a call to an engine may take."),
        new("HuggingFace:Enabled", "bool", false, "engines", "Allow downloads from Hugging Face links."),
        new("Update:Check", "bool", false, "updates", "Look for a newer node and report it."),
        new("Update:Auto", "bool", false, "updates", "Apply a newer node when idle (needs Check)."),
    ];

    private static readonly Dictionary<string, SettingKey> ByKey = Keys.ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);

    public static SettingKey? Find(string key) => ByKey.GetValueOrDefault(key.Trim());

    /// <summary>Whether a configuration key is a secret anywhere it appears — masked whenever it is read back.</summary>
    public static bool IsSecret(string key) =>
        Find(key)?.Type == "secret"
        || key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("ApiKeys", StringComparison.OrdinalIgnoreCase)
        || key.Contains("Secret", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Token", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts a client's value to what the file holds; an error sentence when it does not fit the key.</summary>
    public static JsonNode? Coerce(SettingKey key, JsonNode? value, out string? error)
    {
        error = null;

        if (value is null)
        {
            return null;
        }

        switch (key.Type)
        {
            case "int":
                if (value is JsonValue v && (v.TryGetValue<long>(out _) || (v.TryGetValue<string>(out var text) && long.TryParse(text, out _))))
                {
                    return JsonValue.Create(long.Parse(value.ToString()));
                }

                error = $"{key.Key} takes a whole number";
                return null;

            case "bool":
                if (value is JsonValue b && (b.TryGetValue<bool>(out _) || (b.TryGetValue<string>(out var flag) && bool.TryParse(flag, out _))))
                {
                    return JsonValue.Create(bool.Parse(value.ToString()));
                }

                error = $"{key.Key} takes true or false";
                return null;

            case "enum":
                if (value is JsonValue e && e.TryGetValue<string>(out var choice)
                    && key.Values!.FirstOrDefault(x => string.Equals(x, choice.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known)
                {
                    return JsonValue.Create(known);
                }

                error = $"{key.Key} takes one of {string.Join(", ", key.Values!)}";
                return null;

            case "json":
                if (value is JsonObject or JsonArray)
                {
                    return value.DeepClone();
                }

                error = $"{key.Key} takes a JSON object or array";
                return null;

            default:
                if (value is JsonValue s && s.TryGetValue<string>(out var str))
                {
                    return JsonValue.Create(str);
                }

                error = $"{key.Key} takes text";
                return null;
        }
    }

    /// <summary>The configuration this node would read with <paramref name="localFile"/> in place of today's <c>node.local.json</c>.</summary>
    /// <remarks>The root reloads the providers it is given (its constructor loads each); dispose it when done.</remarks>
    public static ConfigurationRoot WithLocalFile(IConfiguration current, JsonObject localFile, string localFilePath)
    {
        var providers = current is IConfigurationRoot root
            ? root.Providers
                .Where(p => !(p is JsonConfigurationProvider json
                              && string.Equals(Path.GetFileName(json.Source.Path), Path.GetFileName(localFilePath), StringComparison.OrdinalIgnoreCase)))
                .ToList()
            : [];

        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(localFile.ToJsonString()));
        providers.Add(new JsonStreamConfigurationProvider(new JsonStreamConfigurationSource { Stream = stream }));

        return new ConfigurationRoot(providers);
    }

    /// <summary>
    /// Runs the node's own option validators over <paramref name="configuration"/> for every options type they
    /// guard; the failures, one sentence each. Validators that read the live configuration instead of the options
    /// they are handed see today's values — said in the release notes.
    /// </summary>
    public static IReadOnlyList<string> Validate(IServiceProvider services, IConfiguration configuration)
    {
        var failures = new List<string>();

        foreach (var optionsType in ValidatedTypes)
        {
            var section = optionsType.GetField("SectionName", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;

            if (section is null)
            {
                continue;
            }

            var instance = Activator.CreateInstance(optionsType)!;
            configuration.GetSection(section).Bind(instance);

            var validatorType = typeof(IValidateOptions<>).MakeGenericType(optionsType);

            foreach (var validator in (IEnumerable<object>)services.GetServices(validatorType)!)
            {
                var method = validatorType.GetMethod(nameof(IValidateOptions<object>.Validate))!;

                if (method.Invoke(validator, [Options.DefaultName, instance]) is ValidateOptionsResult { Failed: true } result)
                {
                    failures.AddRange(result.Failures ?? [result.FailureMessage]);
                }
            }
        }

        return failures;
    }

    private static readonly Type[] ValidatedTypes =
    [
        typeof(Configuration.NodeOptions),
        typeof(Configuration.CoordinatorOptions),
        typeof(Configuration.LocalApiOptions),
        typeof(Configuration.LocalRetrievalOptions),
        typeof(Backends.Colibri.ColibriOptions),
        typeof(Backends.Strata.StrataOptions),
    ];

    /// <summary>A configuration value as the API shows it: secrets as <c>"(set)"</c>, sections as JSON.</summary>
    public static JsonNode? Show(IConfiguration configuration, SettingKey key)
    {
        var section = configuration.GetSection(key.Key);

        if (IsSecret(key.Key))
        {
            return section.Value is { Length: > 0 } || section.GetChildren().Any() ? JsonValue.Create("(set)") : null;
        }

        if (key.Type == "json")
        {
            return ToJson(section);
        }

        return section.Value is { } value ? JsonValue.Create(value) : null;
    }

    private static JsonNode? ToJson(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();

        if (children.Count == 0)
        {
            return section.Value is { } value ? JsonValue.Create(value) : null;
        }

        if (children.All(c => int.TryParse(c.Key, out _)))
        {
            return new JsonArray([.. children.OrderBy(c => int.Parse(c.Key)).Select(ToJson)]);
        }

        var obj = new JsonObject();

        foreach (var child in children)
        {
            obj[child.Key] = IsSecret(child.Path) && child.Value is { Length: > 0 } ? JsonValue.Create("(set)") : ToJson(child);
        }

        return obj;
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
