using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 124: the supply's records, what the relay answers from them, and the paths an import may read.</summary>
public sealed class RelaySupplyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inferhub-supply-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("qwen3:8b", "library", "qwen3", "8b")]
    [InlineData("qwen3", "library", "qwen3", "latest")]
    [InlineData("someone/Tiny:q4", "someone", "tiny", "q4")]
    [InlineData("registry.ollama.ai/library/qwen3:8b", "library", "qwen3", "8b")]
    public void AnOllamaNameIsTheRegistrysPath(string name, string ns, string model, string tag)
        => Assert.Equal((ns, model, tag), SupplyJobs.ParseOllama(name));

    [Theory]
    [InlineData("hf.co/org/repo:Q4")]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData("a/b/c/d")]
    public void ANameTheSupplyCannotHoldIsRefused(string name)
        => Assert.Throws<ArgumentException>(() => SupplyJobs.ParseOllama(name));

    [Theory]
    [InlineData("model-Q4_K_M.gguf", "*Q4_K_M.gguf", true)]
    [InlineData("sub/model-Q4_K_M.gguf", "*Q4_K_M.gguf", true)]
    [InlineData("sub/model-Q8_0.gguf", "*Q4_K_M.gguf", false)]
    [InlineData("sub/deeper/x.json", "sub/**", true)]
    [InlineData("other/x.json", "sub/*", false)]
    [InlineData("config.json", "config.jso?", true)]
    public void IncludeIsAGlob(string path, string pattern, bool expected)
        => Assert.Equal(expected, SupplyJobs.Matches(path, [pattern]));

    [Fact]
    public void NoIncludeIsEveryFile() => Assert.True(SupplyJobs.Matches("anything/at.all", []));

    [Fact]
    public void AnImportReadsOnlyUnderItsDirectory()
    {
        var root = Path.Combine(directory, "import");

        Assert.Equal(Path.Combine(root, "disk1"), SupplyJobs.ImportPath(root, "disk1"));
        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), SupplyJobs.ImportPath(root, ""));
        Assert.Null(SupplyJobs.ImportPath(root, "../elsewhere"));
        Assert.Null(SupplyJobs.ImportPath(root, "disk1/../../elsewhere"));
        Assert.Null(SupplyJobs.ImportPath(root, Path.GetFullPath(directory)));
        Assert.Null(SupplyJobs.ImportPath(root, "C:/Windows"));
    }

    [Fact]
    public void AnIdIsASafeFileName()
    {
        Assert.Equal("ollama-library-qwen3-8b", RelaySupply.IdFor("ollama", "library/qwen3:8b"));
        Assert.Equal("hf-org-tiny-gguf-main", RelaySupply.IdFor("hf", "org/Tiny-GGUF", "main"));
    }

    [Fact]
    public void AnOllamaModelIsAnsweredByItsManifestAndItsBlobs()
    {
        var supply = Supply();
        var (layer, layerSha) = Blob(supply, "layer");
        var manifest = supply.KeepBytes(Encoding.UTF8.GetBytes("{\"layers\":[]}"));
        supply.Save(Ollama("library/tiny:1b", manifest, new SuppliedFile("application/vnd.ollama.image.model", layer.Length, layerSha)));

        var byTag = supply.Find(NodeRelay.OllamaRegistry, "v2/library/tiny/manifests/1b")!;
        Assert.Equal(manifest, byTag.Sha256);
        Assert.Equal(RelaySupply.OllamaManifestType, byTag.ContentType);
        Assert.Equal(manifest, supply.Find(NodeRelay.OllamaRegistry, $"v2/library/tiny/manifests/sha256:{manifest}")!.Sha256);
        Assert.Equal(layerSha, supply.Find(NodeRelay.OllamaRegistry, $"v2/library/tiny/blobs/sha256:{layerSha}")!.Sha256);

        Assert.Null(supply.Find(NodeRelay.OllamaRegistry, "v2/library/tiny/manifests/2b"));
        // A blob is the model's, not anybody's who names its digest under another repo.
        Assert.Null(supply.Find(NodeRelay.OllamaRegistry, $"v2/library/other/blobs/sha256:{layerSha}"));
        Assert.Null(supply.Find(NodeRelay.OllamaLibrary, "library/tiny"));
    }

    [Fact]
    public void AHuggingFaceRepoIsAnsweredAtItsRevisionAndItsCommitAndNowhereElse()
    {
        var supply = Supply();
        var (bytes, sha) = Blob(supply, "gguf");
        var info = supply.KeepBytes(Encoding.UTF8.GetBytes("{\"pipeline_tag\":\"text-generation\",\"sha\":\"stale\"}"));
        supply.Save(HuggingFace("org/Tiny", "main", Commit, info, new SuppliedFile("sub/tiny.gguf", bytes.Length, sha)));

        using (var revision = Json(supply.Find(NodeRelay.HuggingFace, "api/models/org/Tiny/revision/main")!))
        {
            Assert.Equal(Commit, revision.RootElement.GetProperty("sha").GetString());
            Assert.Equal("text-generation", revision.RootElement.GetProperty("pipeline_tag").GetString());
        }

        Assert.NotNull(supply.Find(NodeRelay.HuggingFace, "api/models/org/tiny"));

        using (var tree = Json(supply.Find(NodeRelay.HuggingFace, $"api/models/org/Tiny/tree/{Commit}")!))
        {
            var file = Assert.Single(tree.RootElement.EnumerateArray());
            Assert.Equal("sub/tiny.gguf", file.GetProperty("path").GetString());
            // Every file carries the sha256 it was checked against, so the node checks it too (98).
            Assert.Equal(sha, file.GetProperty("lfs").GetProperty("oid").GetString());
        }

        var resolve = supply.Find(NodeRelay.HuggingFace, $"org/Tiny/resolve/{Commit}/sub/tiny.gguf")!;
        Assert.Equal(sha, resolve.Sha256);
        Assert.Contains(resolve.Headers, header => header.Key == "X-Repo-Commit" && header.Value == Commit);
        Assert.NotNull(supply.Find(NodeRelay.HuggingFace, "org/Tiny/resolve/main/sub/tiny.gguf"));

        Assert.Null(supply.Find(NodeRelay.HuggingFace, "api/models/org/Tiny/revision/v2"));
        Assert.Null(supply.Find(NodeRelay.HuggingFace, "org/Tiny/resolve/main/other.gguf"));
        Assert.Null(supply.Find(NodeRelay.HuggingFace, "api/models?search=tiny"));
    }

    [Fact]
    public void AModelIsSuppliedWholeOrNotAtAll()
    {
        var supply = Supply();
        var missing = new string('a', 64);

        Assert.Throws<InvalidOperationException>(() => supply.Save(Ollama("library/tiny:1b", missing, new SuppliedFile("x", 1, missing))));
        Assert.Empty(supply.Models());
    }

    [Fact]
    public void ARestartReadsTheRecordsAndSkipsOneWhoseBlobIsGone()
    {
        var supply = Supply();
        var (a, aSha) = Blob(supply, "a");
        var (b, bSha) = Blob(supply, "b");
        var manifest = supply.KeepBytes("{}"u8.ToArray());
        supply.Save(Ollama("library/a:1", manifest, new SuppliedFile("l", a.Length, aSha)));
        supply.Save(Ollama("library/b:1", manifest, new SuppliedFile("l", b.Length, bSha)));
        File.Delete(supply.BlobPath(bSha));

        var restarted = Supply();
        Assert.Equal(["library/a:1"], restarted.Models().Select(model => model.Name));
    }

    [Fact]
    public void RemovingAModelDeletesOnlyTheBlobsNoOtherModelNeeds()
    {
        var supply = Supply();
        var (shared, sharedSha) = Blob(supply, "shared");
        var (own, ownSha) = Blob(supply, "own");
        var manifest = supply.KeepBytes("{}"u8.ToArray());
        supply.Save(Ollama("library/a:1", manifest, new SuppliedFile("l", shared.Length, sharedSha), new SuppliedFile("m", own.Length, ownSha)));
        supply.Save(Ollama("library/b:1", manifest, new SuppliedFile("l", shared.Length, sharedSha)));

        Assert.True(supply.Delete(RelaySupply.IdFor("ollama", "library/a:1")));

        Assert.False(supply.HasBlob(ownSha));
        Assert.True(supply.HasBlob(sharedSha));
        Assert.True(supply.HasBlob(manifest));
        Assert.False(supply.Delete("nothing"));
    }

    [Fact]
    public void OffIsNothing()
    {
        var supply = new RelaySupply(new Fixed(new RelayOptions { Enabled = true, Supply = { DataDirectory = directory } }), NullLogger<RelaySupply>.Instance);

        Assert.Null(supply.Find(NodeRelay.OllamaRegistry, "v2/library/tiny/manifests/1b"));
        Assert.Null(supply.Snapshot(0));
        Assert.False(supply.Only);
        Assert.False(Directory.Exists(directory));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    private RelaySupply Supply()
        => new(new Fixed(new RelayOptions { Enabled = true, Supply = { Enabled = true, DataDirectory = directory, KeepFreeGiB = 0 } }), NullLogger<RelaySupply>.Instance);

    private static (byte[] Bytes, string Sha) Blob(RelaySupply supply, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var sha = supply.KeepBytes(bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha);
        return (bytes, sha);
    }

    private static SuppliedModel Ollama(string name, string manifest, params SuppliedFile[] files)
        => new(RelaySupply.IdFor("ollama", name), SuppliedModel.Ollama, name, name.Split(':')[1], null, manifest, null, null,
            files, files.Sum(file => file.Size), "fetched", "test", DateTimeOffset.UtcNow);

    private static SuppliedModel HuggingFace(string repo, string revision, string commit, string? info, params SuppliedFile[] files)
        => new(RelaySupply.IdFor("hf", repo, revision), SuppliedModel.HuggingFace, repo, revision, commit, null, null, info,
            files, files.Sum(file => file.Size), "fetched", "test", DateTimeOffset.UtcNow);

    private static JsonDocument Json(SupplyAnswer answer) => JsonDocument.Parse(answer.Body!);

    private sealed class Fixed(RelayOptions value) : IOptionsMonitor<RelayOptions>
    {
        public RelayOptions CurrentValue => value;

        public RelayOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RelayOptions, string?> listener) => null;
    }
}
