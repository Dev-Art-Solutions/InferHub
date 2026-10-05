using System.Text.RegularExpressions;
using InferHub.Node.Backends.Colibri;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Backends;

/// <summary>
/// <c>Backend:Engines</c> (phase 95), refused at startup rather than discovered in front of a user.
/// A single-backend node is not this validator's business — <c>UpstreamBackendOptionsValidator</c>
/// still owns <c>Backend:Type</c> — so an empty <c>Engines</c> is success without reading anything.
/// </summary>
public sealed partial class BackendOptionsValidator(IConfiguration configuration) : IValidateOptions<BackendOptions>
{
    /// <summary>
    /// A name travels in a URL path (<c>/api/admin/nodes/{id}/backends/{name}/start</c>) and as a
    /// profile key, so it is a plain token — never a path, never a command (43 D1's hostile-input
    /// table, one field over).
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex EngineName();

    public ValidateOptionsResult Validate(string? name, BackendOptions options)
    {
        if (!options.IsMulti)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        const string Section = $"{BackendOptions.SectionName}:{nameof(BackendOptions.Engines)}";

        // 95 D1. Engines replaces the single type. The shipped appsettings.json says `ollama`, which is
        // the default and says nothing, so it is ignored; any other value (the :colibri image sets
        // `colibri`) is a second answer to "what does this node run", and binder order is not how
        // that gets decided.
        var singleType = configuration[$"{BackendOptions.SectionName}:{nameof(BackendOptions.Type)}"];

        if (!string.IsNullOrWhiteSpace(singleType)
            && !string.Equals(singleType.Trim(), BackendOptions.Ollama, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"{BackendOptions.SectionName}:{nameof(BackendOptions.Type)} is '{singleType}' and {Section} is set. "
                + $"{Section} replaces the single backend type; name each engine there and clear {BackendOptions.SectionName}:{nameof(BackendOptions.Type)} "
                + "(in a container: Backend__Type=).");
        }

        if (options.StopDrain < TimeSpan.Zero)
        {
            failures.Add($"{BackendOptions.SectionName}:{nameof(BackendOptions.StopDrain)} must not be negative (got {options.StopDrain}).");
        }

        var colibri = configuration.GetSection(ColibriOptions.SectionName).Get<ColibriOptions>() ?? new ColibriOptions();
        var ports = new Dictionary<int, string>();

        foreach (var (engineName, engine) in options.Engines)
        {
            var key = $"{Section}:{engineName}";
            var type = engine.NormalizedType();

            if (!EngineName().IsMatch(engineName ?? string.Empty))
            {
                failures.Add($"{key}: an engine name is letters, digits, '.', '_' and '-', starting with a letter or digit, at most 64 characters.");
                continue;
            }

            if (!BackendOptions.EngineTypes.Contains(type))
            {
                failures.Add(
                    $"{key}:Type is '{engine.Type}'. An engine is one of: {string.Join(", ", BackendOptions.EngineTypes)}. "
                    + "A cloud vendor is not an engine a node starts or stops; run it as the node's single Backend:Type.");
                continue;
            }

            if (engine.TimeoutSeconds <= 0)
            {
                failures.Add($"{key}:TimeoutSeconds must be greater than zero (got {engine.TimeoutSeconds}).");
            }

            if (!string.IsNullOrWhiteSpace(engine.BaseUrl)
                && (!Uri.TryCreate(engine.BaseUrl, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
            {
                failures.Add($"{key}:BaseUrl must be an absolute http(s) URL (got '{engine.BaseUrl}').");
            }

            if (type != BackendOptions.LlamaCpp && engine.Serve.IsEnabled)
            {
                failures.Add(
                    $"{key}:Serve is for llamacpp engines. "
                    + (type == BackendOptions.Colibri ? $"A colibri engine is launched by {ColibriOptions.SectionName}:Serve:Model." : "This engine type is not launched by the node."));
            }

            switch (type)
            {
                case BackendOptions.Ollama when !string.IsNullOrWhiteSpace(engine.BaseUrl):
                    failures.Add($"{key}:BaseUrl is not read for an ollama engine; its address is Ollama:Endpoint.");
                    break;

                case BackendOptions.OpenAi when string.IsNullOrWhiteSpace(engine.BaseUrl):
                    failures.Add($"{key}:BaseUrl must be set for an openai engine; the node does not launch one.");
                    break;

                case BackendOptions.Colibri when colibri.Serve.IsEnabled && !string.IsNullOrWhiteSpace(engine.BaseUrl):
                    failures.Add(
                        $"{key}:BaseUrl and {ColibriOptions.SectionName}:Serve:Model are both set. "
                        + $"A launched engine listens on {colibri.LaunchedBaseUrl()}; set one or the other.");
                    break;

                case BackendOptions.Colibri when colibri.Serve.IsEnabled:
                    Claim(ports, colibri.Serve.Port, key, failures);
                    break;

                case BackendOptions.LlamaCpp when engine.Serve.IsEnabled:
                    if (!string.IsNullOrWhiteSpace(engine.BaseUrl))
                    {
                        failures.Add(
                            $"{key}:BaseUrl and {key}:Serve:Model are both set. "
                            + $"A launched llama-server listens on {engine.Serve.LaunchedBaseUrl()}; set one or the other.");
                    }

                    if (engine.Serve.Port is < 1 or > 65535)
                    {
                        failures.Add($"{key}:Serve:Port must be a TCP port (got {engine.Serve.Port}).");
                    }
                    else
                    {
                        Claim(ports, engine.Serve.Port, key, failures);
                    }

                    if (string.IsNullOrWhiteSpace(engine.Serve.Executable))
                    {
                        failures.Add($"{key}:Serve:Executable must name the llama-server binary.");
                    }

                    // Rule 7. This process's output is logged by the node.
                    foreach (var flag in engine.Serve.Arguments
                                 .Select(a => a?.Trim() ?? string.Empty)
                                 .Where(a => LlamaCppServe.ContentLoggingFlags.Contains(a.Split('=')[0], StringComparer.Ordinal)))
                    {
                        failures.Add(
                            $"{key}:Serve:Arguments names '{flag}', which makes llama-server log prompts, and this node logs "
                            + "that process's output. No prompt reaches a log on this node (design rule 7).");
                    }

                    break;
            }
        }

        // 95 D2: the two engines whose configuration is a whole section of its own are one each.
        foreach (var single in new[] { BackendOptions.Ollama, BackendOptions.Colibri })
        {
            var named = options.Engines
                .Where(pair => pair.Value.NormalizedType() == single)
                .Select(pair => pair.Key)
                .ToArray();

            if (named.Length > 1)
            {
                failures.Add(
                    $"{Section} names {named.Length} {single} engines ({string.Join(", ", named)}). "
                    + $"A node runs at most one: its configuration is the {(single == BackendOptions.Ollama ? "Ollama:" : "Colibri:")} section, and there is one of those.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Claim(Dictionary<int, string> ports, int port, string key, List<string> failures)
    {
        if (ports.TryGetValue(port, out var holder))
        {
            failures.Add($"{key} and {holder} would both launch an engine on port {port}.");
            return;
        }

        ports[port] = key;
    }
}
