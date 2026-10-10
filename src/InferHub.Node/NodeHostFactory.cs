using InferHub.Node.Configuration;
using InferHub.Node.LocalApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace InferHub.Node;

/// <summary>
/// Picks the host shape for both node entry points (phase 37): a web host when solo mode is on, the
/// plain worker host otherwise.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the same reason <see cref="NodeHostBuilderExtensions.AddInferHubNode"/> does —
/// the console host and the Windows-service host must not drift, and "which builder do we make"
/// is now a decision, so it belongs in one place rather than copied into two <c>Program.cs</c>
/// files.
/// </para>
/// <para>
/// The pleasant part is what did <em>not</em> have to change: <c>WebApplicationBuilder</c>
/// implements <c>IHostApplicationBuilder</c>, so <c>AddInferHubNode</c> keeps its signature and
/// every existing registration, and <c>NodeCompositionTests</c> still guards one composition root
/// rather than two.
/// </para>
/// <para>
/// <strong>A node with solo mode off must not pay for any of this.</strong> No Kestrel, no
/// listening socket, no routing middleware — the default node is the v3.4 worker exactly.
/// </para>
/// </remarks>
public static class NodeHostFactory
{
    /// <param name="args">The process arguments.</param>
    /// <param name="settingsFile">
    /// Phase 101, D1: an optional JSON file layered after <c>appsettings*.json</c> and before environment
    /// variables — the Windows setup's <c>%ProgramData%\InferHub\Node\node.settings.json</c>. Null for the
    /// console host, which has none.
    /// </param>
    public static IHostApplicationBuilder Create(string[] args, string? settingsFile = null)
    {
        // Read before the options system exists — this decides which builder to construct, so it
        // cannot come from DI. The settings file is in it too: a setup that chose solo mode wrote
        // LocalApi:Enabled there, and a pre-read that missed it would build the worker host.
        var solo = new ConfigurationBuilder()
            .AddInferHubNodeConfigurationSources(args, settingsFile)
            .Build()
            .GetSection(LocalApiOptions.SectionName)
            .GetValue<bool>(nameof(LocalApiOptions.Enabled));

        if (!solo)
        {
            var worker = Host.CreateApplicationBuilder(args);
            InsertSettingsFile(worker.Configuration, settingsFile);
            return worker;
        }

        var web = WebApplication.CreateBuilder(args);
        InsertSettingsFile(web.Configuration, settingsFile);

        var localApi = web.Configuration
            .GetSection(LocalApiOptions.SectionName)
            .Get<LocalApiOptions>() ?? new LocalApiOptions();

        // Set the URLs on the web host directly rather than leaving them to the `Urls` config key.
        // Phase-21 D6 is the reason: an `appsettings.json` value for `Urls` *overrides* the
        // ASPNETCORE_-prefixed provider, and a container honouring only ASPNETCORE_URLS would bind
        // loopback and answer nobody. Solo mode's address has its own key and is applied here, so
        // LocalApi__Urls always wins and `-p` actually reaches something.
        web.WebHost.UseUrls([.. localApi.SplitUrls()]);

        return web;
    }

    /// <summary>Builds the host and maps the local API when there is one.</summary>
    public static IHost Build(IHostApplicationBuilder builder)
    {
        if (builder is not WebApplicationBuilder web)
        {
            return ((HostApplicationBuilder)builder).Build();
        }

        var app = web.Build();
        app.MapInferHubLocalApi();
        return app;
    }

    /// <summary>
    /// The same sources <c>Host.CreateApplicationBuilder</c> would add, so the pre-flight read of
    /// <c>LocalApi:Enabled</c> sees exactly what the real host will — including the environment
    /// variable and command-line forms an operator is most likely to use for a switch like this.
    /// </summary>
    private static IConfigurationBuilder AddInferHubNodeConfigurationSources(
        this IConfigurationBuilder configuration,
        string[] args,
        string? settingsFile)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(settingsFile))
        {
            configuration.AddJsonFile(settingsFile, optional: true, reloadOnChange: false);
        }

        return configuration
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }

    /// <summary>
    /// Puts the settings file right after the host's last <c>appsettings*.json</c>, so environment variables
    /// and the command line still win over what a setup wrote (D1) — the order an operator expects.
    /// </summary>
    internal static void InsertSettingsFile(IConfigurationBuilder configuration, string? settingsFile)
    {
        if (string.IsNullOrWhiteSpace(settingsFile))
        {
            return;
        }

        var sources = configuration.Sources;
        var index = 0;

        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path: { } path }
                && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                index = i + 1;
            }
        }

        var source = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
        {
            Path = settingsFile,
            Optional = true,
            ReloadOnChange = false,
        };
        source.ResolveFileProvider();
        sources.Insert(index, source);
    }
}
