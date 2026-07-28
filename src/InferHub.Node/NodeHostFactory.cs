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
    public static IHostApplicationBuilder Create(string[] args)
    {
        // Read before the options system exists — this decides which builder to construct, so it
        // cannot come from DI.
        var solo = new ConfigurationBuilder()
            .AddInferHubNodeConfigurationSources(args)
            .Build()
            .GetSection(LocalApiOptions.SectionName)
            .GetValue<bool>(nameof(LocalApiOptions.Enabled));

        if (!solo)
        {
            return Host.CreateApplicationBuilder(args);
        }

        var web = WebApplication.CreateBuilder(args);

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
        string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";

        return configuration
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);
    }
}
