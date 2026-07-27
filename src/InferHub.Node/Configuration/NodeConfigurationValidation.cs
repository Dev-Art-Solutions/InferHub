using InferHub.Node.Backends;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Configuration;

public sealed class CoordinatorOptionsValidator : IValidateOptions<CoordinatorOptions>
{
    public ValidateOptionsResult Validate(string? name, CoordinatorOptions options)
    {
        var failures = new List<string>();

        // Endpoints falls back to Url, so validating the resolved list covers both shapes and
        // cannot let a typo in an HA list boot a node that then silently only ever reaches one hub.
        var endpoints = options.Endpoints.Count > 0
            ? nameof(CoordinatorOptions.Endpoints)
            : nameof(CoordinatorOptions.Url);

        if (options.ResolvedEndpoints().Count == 0 || options.ResolvedEndpoints().Any(string.IsNullOrWhiteSpace))
        {
            failures.Add($"{CoordinatorOptions.SectionName}:{endpoints} must be set.");
        }

        foreach (var endpoint in options.ResolvedEndpoints().Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add(
                    $"{CoordinatorOptions.SectionName}:{endpoints} must be absolute http(s) URLs (got '{endpoint}').");
            }
        }

        if (options.HeartbeatInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.HeartbeatInterval)} must be positive (got {options.HeartbeatInterval}).");
        }

        if (options.ModelRefreshInterval <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.ModelRefreshInterval)} must be positive (got {options.ModelRefreshInterval}).");
        }

        if (options.RetryDelay <= TimeSpan.Zero)
        {
            failures.Add(
                $"{CoordinatorOptions.SectionName}:{nameof(CoordinatorOptions.RetryDelay)} must be positive (got {options.RetryDelay}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class NodeOptionsValidator : IValidateOptions<NodeOptions>
{
    public ValidateOptionsResult Validate(string? name, NodeOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Name))
        {
            failures.Add($"{NodeOptions.SectionName}:{nameof(NodeOptions.Name)} must be set.");
        }

        if (options.MaxConcurrency is { } cap && cap < 1)
        {
            failures.Add(
                $"{NodeOptions.SectionName}:{nameof(NodeOptions.MaxConcurrency)} must be >= 1 when set (got {cap}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Only bites when <c>Backend:Type=openai</c> — an Ollama node has no upstream to configure.
/// A node that boots and then 500s on every job it is handed is worse than a node that refuses
/// to boot and says which key is missing.
/// </summary>
public sealed class OpenAiBackendOptionsValidator(IOptions<BackendOptions> backend)
    : IValidateOptions<OpenAiBackendOptions>
{
    public ValidateOptionsResult Validate(string? name, OpenAiBackendOptions options)
    {
        if (backend.Value.Normalized() != BackendOptions.OpenAi)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.BaseUrl)} must be set when {BackendOptions.SectionName}:{nameof(BackendOptions.Type)}=openai.");
        }
        else if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.BaseUrl)} must be an absolute http(s) URL (got '{options.BaseUrl}').");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add(
                $"{OpenAiBackendOptions.SectionName}:{nameof(OpenAiBackendOptions.TimeoutSeconds)} must be greater than zero (got {options.TimeoutSeconds}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

public sealed class OllamaOptionsValidator : IValidateOptions<OllamaOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Endpoint)} must be set.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.Endpoint)} must be an absolute http(s) URL (got '{options.Endpoint}').");
        }

        if (options.RequestTimeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail(
                $"{OllamaOptions.SectionName}:{nameof(OllamaOptions.RequestTimeout)} must be greater than zero (got '{options.RequestTimeout}').");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Only bites when the supervisor is switched on. A node that leaves it off must boot exactly
/// as it did before the feature existed, including past a section somebody half-edited.
/// </summary>
public sealed class OllamaSupervisorOptionsValidator : IValidateOptions<OllamaSupervisorOptions>
{
    public ValidateOptionsResult Validate(string? name, OllamaSupervisorOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        Positive(options.ProbeInterval, nameof(OllamaSupervisorOptions.ProbeInterval));
        Positive(options.ProbeTimeout, nameof(OllamaSupervisorOptions.ProbeTimeout));
        Positive(options.ReadyTimeout, nameof(OllamaSupervisorOptions.ReadyTimeout));
        Positive(options.RestartWindow, nameof(OllamaSupervisorOptions.RestartWindow));
        Positive(options.RestartBackoff, nameof(OllamaSupervisorOptions.RestartBackoff));

        // A probe that outlives its own tick makes the consecutive-failure threshold meaningless:
        // the ticks would overlap and "three in a row" would stop meaning three intervals.
        if (options.ProbeTimeout >= options.ProbeInterval)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ProbeTimeout)} must be shorter than {nameof(OllamaSupervisorOptions.ProbeInterval)} (got {options.ProbeTimeout} >= {options.ProbeInterval}).");
        }

        if (options.ReadyTimeout <= options.ProbeTimeout)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.ReadyTimeout)} must be longer than {nameof(OllamaSupervisorOptions.ProbeTimeout)} (got {options.ReadyTimeout} <= {options.ProbeTimeout}).");
        }

        if (options.UnhealthyThreshold < 1)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.UnhealthyThreshold)} must be >= 1 (got {options.UnhealthyThreshold}).");
        }

        if (options.MaxRestartAttempts < 1)
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.MaxRestartAttempts)} must be >= 1 (got {options.MaxRestartAttempts}).");
        }

        if (!string.IsNullOrWhiteSpace(options.InstallUrl)
            && (!Uri.TryCreate(options.InstallUrl, UriKind.Absolute, out var installUri)
                || (installUri.Scheme != Uri.UriSchemeHttp && installUri.Scheme != Uri.UriSchemeHttps)))
        {
            failures.Add(
                $"{OllamaSupervisorOptions.SectionName}:{nameof(OllamaSupervisorOptions.InstallUrl)} must be an absolute http(s) URL when set (got '{options.InstallUrl}').");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);

        void Positive(TimeSpan value, string key)
        {
            if (value <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{OllamaSupervisorOptions.SectionName}:{key} must be greater than zero (got {value}).");
            }
        }
    }
}
