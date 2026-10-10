using System.Text.Json.Nodes;
using InferHub.Node;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Configuration;

namespace InferHub.Tests;

/// <summary>
/// Phase 101, D1: the setup's answers, merged into <c>node.settings.json</c> by the exe's <c>configure</c> verb,
/// and layered between <c>appsettings*.json</c> and environment variables.
/// </summary>
public class NodeSettingsFileTests : IDisposable
{
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "inferhub-settings-" + Guid.NewGuid().ToString("N"));

    public NodeSettingsFileTests() => Directory.CreateDirectory(scratch);

    public void Dispose() => Directory.Delete(scratch, recursive: true);

    [Fact]
    public void TheSetupsAnswersBecomeTypedNestedJson()
    {
        var path = Path.Combine(scratch, "sub", NodeSettingsFile.FileName);

        NodeSettingsFile.Apply(path, NodeSettingsFile.ParseInput("""
            # written by the setup
            Coordinator:Url=https://hub.example:5080/
            Coordinator:EnrollmentSecret=s3cr=t
            Node:Name=Тестов възел
            Node:MaxConcurrency=4
            Node:Labels:gpu=3090
            Update:Auto=true
            Node:DataDirectory=C:\ProgramData\InferHub\Node
            """));

        var json = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("https://hub.example:5080/", (string?)json["Coordinator"]!["Url"]);
        Assert.Equal("s3cr=t", (string?)json["Coordinator"]!["EnrollmentSecret"]);
        Assert.Equal("Тестов възел", (string?)json["Node"]!["Name"]);
        Assert.Equal(4, (long)json["Node"]!["MaxConcurrency"]!);
        Assert.Equal("3090", json["Node"]!["Labels"]!["gpu"]!.ToString());
        Assert.True((bool)json["Update"]!["Auto"]!);
        Assert.Equal(@"C:\ProgramData\InferHub\Node", (string?)json["Node"]!["DataDirectory"]);

        // And the configuration system reads what the node will read.
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();
        Assert.Equal("Тестов възел", configuration["Node:Name"]);
        Assert.Equal("4", configuration["node:maxconcurrency"]);
        Assert.Equal("3090", configuration["Node:Labels:gpu"]);
    }

    [Fact]
    public void ARerunMergesKeepsWhatItWasNotGivenAndReplacesASetWhenAskedTo()
    {
        var path = Path.Combine(scratch, NodeSettingsFile.FileName);
        File.WriteAllText(path, """
            {
              "coordinator": { "url": "http://old/", "EnrollmentSecret": "keep-me" },
              "Node": { "Labels": { "room": "lab", "gpu": "1080" }, "MaxConcurrency": 2 },
              "Backend": { "Engines": { "llama": { "Type": "llamacpp" } } }
            }
            """);

        NodeSettingsFile.Apply(path, NodeSettingsFile.ParseInput("""
            Coordinator:Url=http://new/
            Node:MaxConcurrency=
            Node:Labels=
            Node:Labels:gpu=3090
            """));

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        // Existing case is kept (configuration keys are case-insensitive), so no duplicate section appears.
        Assert.Equal("http://new/", (string?)json["coordinator"]!["url"]);
        Assert.Null(json["Coordinator"]);
        Assert.Equal("keep-me", (string?)json["coordinator"]!["EnrollmentSecret"]);
        Assert.Null(json["Node"]!["MaxConcurrency"]);
        Assert.Equal(["gpu"], json["Node"]!["Labels"]!.AsObject().Select(p => p.Key));
        Assert.Equal("llamacpp", (string?)json["Backend"]!["Engines"]!["llama"]!["Type"]);
    }

    [Fact]
    public void ALineThatIsNotKeyValueIsRefused()
    {
        Assert.Throws<FormatException>(() => NodeSettingsFile.ParseInput("just words"));
        Assert.Throws<FormatException>(() => NodeSettingsFile.ParseInput("=value"));
    }

    [Fact]
    public void TheSettingsFileSitsBetweenAppsettingsAndTheEnvironment()
    {
        File.WriteAllText(Path.Combine(scratch, "appsettings.json"), """{ "Node": { "Name": "from-appsettings", "MaxConcurrency": 1 } }""");
        var settings = Path.Combine(scratch, NodeSettingsFile.FileName);
        File.WriteAllText(settings, """{ "Node": { "Name": "from-setup", "MaxConcurrency": 3 } }""");

        var configuration = new ConfigurationManager();
        configuration.SetBasePath(scratch);
        configuration.AddJsonFile("appsettings.json", optional: true);
        // Stands in for environment variables: added after the json files, as the host does.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Name"] = "from-environment" });

        NodeHostFactory.InsertSettingsFile(configuration, settings);

        Assert.Equal("from-environment", configuration["Node:Name"]);
        Assert.Equal("3", configuration["Node:MaxConcurrency"]);
    }

    [Fact]
    public void NoSettingsFileChangesNothing()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Node:Name"] = "x" });
        var before = configuration.Sources.Count;

        NodeHostFactory.InsertSettingsFile(configuration, null);

        Assert.Equal(before, configuration.Sources.Count);
    }
}
