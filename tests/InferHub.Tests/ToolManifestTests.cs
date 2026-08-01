using InferHub.Node.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>
/// The manifest loader's refusals. Each one exists because the alternative is a tool that starts
/// and is subtly wrong, which is always worse than one that does not start and says why.
/// </summary>
public class ToolManifestTests
{
    [Fact]
    public void ACommandThatIsAStringIsRefusedByName()
    {
        var ok = ToolManifestLoader.TryParse(
            """
            {
              "id": "whisper",
              "capabilities": [ { "kind": "transcribe", "models": ["whisper-small"] } ],
              "command": "/usr/bin/python3 -u whisper_worker.py"
            }
            """,
            "whisper.json",
            out _,
            out var error);

        Assert.False(ok);

        // Named, not just rejected: every shell, every CI config and every Docker CMD accepts a
        // string, so this is the mistake people make first and the message has to say which field.
        Assert.Contains("'command' is a string", error);
        Assert.Contains("argv array", error);
    }

    [Fact]
    public void AGoodManifestParsesWithTheDocumentedDefaults()
    {
        var ok = ToolManifestLoader.TryParse(
            """
            {
              "id": "whisper",
              "capabilities": [ { "kind": "transcribe", "models": ["whisper-small", "whisper-large-v3"] } ],
              "command": ["/opt/inferhub/venv/bin/python", "-u", "/opt/inferhub/tools/whisper_worker.py"],
              "workdir": "/opt/inferhub/tools",
              "env": { "HF_HOME": "/data/tools/hf" }
            }
            """,
            "whisper.json",
            out var manifest,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("whisper", manifest!.Id);
        Assert.Equal(3, manifest.Command.Count);
        Assert.Equal("/opt/inferhub/venv/bin/python", manifest.Command[0]);
        Assert.Equal("/data/tools/hf", manifest.Environment["HF_HOME"]);

        // The defaults the README documents. MaxWorkers 1 is the load-bearing one: two copies of a
        // model on one card is a memory error at the worst possible moment.
        Assert.Equal(0, manifest.MinWorkers);
        Assert.Equal(1, manifest.MaxWorkers);
        Assert.Equal(120, manifest.StartTimeoutSeconds);
        Assert.Equal(600, manifest.RequestTimeoutSeconds);
        Assert.Equal(900, manifest.IdleTimeoutSeconds);

        Assert.True(manifest.Provides("transcribe", "whisper-large-v3"));
        Assert.False(manifest.Provides("speak", "whisper-small"));
        Assert.False(manifest.Provides("transcribe", "whisper-medium"));
    }

    [Theory]
    [InlineData("""{ "capabilities": [{"kind":"a","models":["b"]}], "command": ["x"] }""", "'id' is required")]
    [InlineData("""{ "id": "a", "command": ["x"] }""", "'capabilities'")]
    [InlineData("""{ "id": "a", "capabilities": [{"kind":"a","models":["b"]}] }""", "'command' is required")]
    [InlineData("""{ "id": "a", "capabilities": [{"kind":"a","models":["b"]}], "command": [] }""", "non-empty")]
    [InlineData("""{ "id": "a", "capabilities": [{"kind":"a","models":["b"]}], "command": ["x"], "maxWorkers": 0 }""", "'maxWorkers'")]
    [InlineData("""{ "id": "a", "capabilities": [{"kind":"a","models":["b"]}], "command": ["x"], "minWorkers": 3 }""", "'minWorkers'")]
    [InlineData("""{ "id": "a", "capabilities": [{"kind":"a","models":["b"]}], "command": ["x"], "startTimeoutSeconds": 0 }""", "'startTimeoutSeconds'")]
    [InlineData("not json at all", "not valid JSON")]
    public void EachRefusalNamesTheFieldThatCausedIt(string json, string expected)
    {
        Assert.False(ToolManifestLoader.TryParse(json, "t.json", out _, out var error));
        Assert.Contains(expected, error);
    }

    /// <summary>
    /// Phase 42: <c>"models": []</c> is a deliberate open set and <c>models</c> omitted is a
    /// mistake, so the two are distinguished by null-versus-empty rather than collapsed. The shipped
    /// Piper manifest is the reason — its models are voice files an operator dropped into a
    /// directory, and there is no list anybody could write in advance that survives the first new
    /// voice.
    /// </summary>
    [Fact]
    public void AnEmptyModelListIsAnOpenSetAndAMissingOneIsStillAMistake()
    {
        Assert.True(ToolManifestLoader.TryParse(
            """{ "id": "piper", "capabilities": [{"kind":"speak","models":[]}], "command": ["x"] }""",
            "piper.json",
            out var manifest,
            out _));

        var capability = Assert.Single(manifest!.Capabilities);
        Assert.Equal("speak", capability.Kind);
        Assert.Empty(capability.Models);

        Assert.False(ToolManifestLoader.TryParse(
            """{ "id": "piper", "capabilities": [{"kind":"speak"}], "command": ["x"] }""",
            "piper.json",
            out _,
            out var error));

        Assert.Contains("\"models\": []", error);
    }

    /// <summary>The two manifests that ship in the <c>:tools</c> image must actually load.</summary>
    [Theory]
    [InlineData("whisper.json", "whisper", "transcribe")]
    [InlineData("piper.json", "piper", "speak")]
    public void TheShippedManifestsParse(string file, string id, string kind)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "InferHub.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var text = File.ReadAllText(Path.Combine(directory!.FullName, "python", "manifests", file));

        Assert.True(ToolManifestLoader.TryParse(text, file, out var manifest, out var error), error);
        Assert.Equal(id, manifest!.Id);
        Assert.Equal(kind, Assert.Single(manifest.Capabilities).Kind);

        // argv, never a command line — the field the loader refuses by name (phase-41 D3).
        Assert.True(manifest.Command.Count > 1);

        // One worker per manifest by default: a second Whisper on the same card is two copies of
        // the weights and an out-of-memory error at the worst possible moment.
        Assert.Equal(1, manifest.MaxWorkers);
    }

    /// <summary>
    /// One bad manifest must not take a node's inference offline. The box still has a GPU and a
    /// backend, and a fleet that loses a chat node to a fat-fingered JSON comma has traded a small
    /// problem for a large one.
    /// </summary>
    [Fact]
    public void ABadManifestIsSkippedAndTheGoodOnesInTheSameDirectoryStillLoad()
    {
        using var directory = new ToolWorkerFixture.TempDirectory("inferhub-manifests");

        File.WriteAllText(Path.Combine(directory.Path, "broken.json"), "{ not json");
        directory.WriteManifest("good.json", new
        {
            id = "good",
            capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
            command = new[] { "/bin/true" }
        });

        var captured = new CapturingLoggerProvider();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(captured));

        var manifests = ToolManifestLoader.LoadDirectory(directory.Path, factory.CreateLogger("t"));

        Assert.Equal("good", Assert.Single(manifests).Id);
        Assert.True(captured.Contains("is not usable"));
    }

    [Fact]
    public void TwoManifestsWithTheSameIdAreRefusedBecauseToolsAllowedWouldBeAmbiguous()
    {
        using var directory = new ToolWorkerFixture.TempDirectory("inferhub-manifests");

        foreach (var file in new[] { "a.json", "b.json" })
        {
            directory.WriteManifest(file, new
            {
                id = "same",
                capabilities = new[] { new { kind = "echo", models = new[] { "echo" } } },
                command = new[] { "/bin/true" }
            });
        }

        var manifests = ToolManifestLoader.LoadDirectory(directory.Path, NullLogger.Instance);

        Assert.Single(manifests);
    }

    [Fact]
    public void AMissingManifestDirectoryIsNotAnError()
    {
        var manifests = ToolManifestLoader.LoadDirectory(
            Path.Combine(Path.GetTempPath(), $"inferhub-missing-{Guid.NewGuid():N}"),
            NullLogger.Instance);

        Assert.Empty(manifests);
    }
}
