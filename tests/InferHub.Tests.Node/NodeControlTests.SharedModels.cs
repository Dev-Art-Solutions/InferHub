using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Control;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace InferHub.Tests;

/// <summary>Phase 117: models kept for the owner's machine — what the network may use, and what it is never told of.</summary>
public sealed partial class NodeControlTests
{
    private static readonly ModelInfo[] ThreeModels =
    [
        new("llama3", "sha256:a", 1),
        new("qwen3:32b", "sha256:b", 2),
        new("nomic-embed-text", "sha256:c", 3)
    ];

    [Fact]
    public void WithNoListTheNetworkMayUseEveryModel()
    {
        var (sharing, _, _, _) = Terms(new SharingOptions(), Monday);

        Assert.Equal(ThreeModels, sharing.Shared(ThreeModels));
        Assert.True(sharing.IsShared("qwen3:32b"));
    }

    [Fact]
    public void ExcludeKeepsAModelAndIncludeSharesOnlyThose()
    {
        var (kept, _, _, _) = Terms(new SharingOptions { Models = new ModelFilterOptions { Exclude = ["QWEN3:32b "] } }, Monday);

        Assert.Equal(["llama3", "nomic-embed-text"], kept.Shared(ThreeModels).Select(m => m.Name));
        Assert.False(kept.IsShared("qwen3:32b"));

        var (only, _, _, _) = Terms(new SharingOptions { Models = new ModelFilterOptions { Include = ["llama3"] } }, Monday);

        Assert.Equal(["llama3"], only.Shared(ThreeModels).Select(m => m.Name));
        Assert.False(only.IsShared("nomic-embed-text"));
    }

    [Fact]
    public void AChangedListIsAnnouncedOnceAndTheSameListIsNot()
    {
        var (sharing, _, options, _) = Terms(new SharingOptions(), Monday);
        var announced = 0;
        sharing.SharedModelsChanged += () => announced++;

        options.Set(new NodeOptions { Sharing = new SharingOptions { TimeZone = "UTC", Models = new ModelFilterOptions { Exclude = ["qwen3:32b"] } } });
        options.Set(new NodeOptions { Sharing = new SharingOptions { TimeZone = "UTC", Models = new ModelFilterOptions { Exclude = [" Qwen3:32B"] } } });
        Assert.Equal(1, announced);

        // Another term changing is not a change of models.
        options.Set(new NodeOptions { Sharing = new SharingOptions { TimeZone = "UTC", DailyNetworkMinutes = 5, Models = new ModelFilterOptions { Exclude = ["qwen3:32b"] } } });
        Assert.Equal(1, announced);

        options.Set(new NodeOptions { Sharing = new SharingOptions { TimeZone = "UTC" } });
        Assert.Equal(2, announced);
    }

    [Fact]
    public void AModelInBothListsFailsStartup()
    {
        var result = new NodeOptionsValidator().Validate(null, new NodeOptions
        {
            Name = "n",
            Sharing = new SharingOptions { Models = new ModelFilterOptions { Include = ["llama3"], Exclude = ["LLAMA3"] } }
        });

        Assert.False(result.Succeeded);
        Assert.Contains("Node:Sharing:Models", result.FailureMessage);
    }

    [Fact]
    public async Task AModelIsKeptLiveThroughTheSharingRouteAndEachModelSaysWhetherItIsShared()
    {
        var (host, client, dir) = await ControlHostAsync();
        await using var owned = host;
        host.Backend.Models = ThreeModels;
        var sharing = host.Services.GetRequiredService<NodeSharing>();

        var kept = await client.PutAsJsonAsync("/api/control/sharing", new { models = new { exclude = new[] { "qwen3:32b" } } });
        Assert.True(kept.StatusCode == HttpStatusCode.OK, await kept.Content.ReadAsStringAsync());
        await WaitUntilAsync(() => !sharing.IsShared("qwen3:32b"));

        var file = NodeLocalSettings.Read(Path.Combine(dir, "node.local.json"));
        Assert.Equal("qwen3:32b", file["Node"]!["Sharing"]!["Models"]!["Exclude"]![0]!.GetValue<string>());

        using (var models = JsonDocument.Parse(await client.GetStringAsync("/api/control/models")))
        {
            var shared = models.RootElement.GetProperty("models").EnumerateArray()
                .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("shared").GetBoolean());
            Assert.Equal(new Dictionary<string, bool> { ["llama3"] = true, ["qwen3:32b"] = false, ["nomic-embed-text"] = true }, shared);
            Assert.Equal("qwen3:32b", models.RootElement.GetProperty("sharing").GetProperty("exclude")[0].GetString());
        }

        using (var status = JsonDocument.Parse(await client.GetStringAsync("/api/control/status")))
        {
            Assert.Equal("qwen3:32b", status.RootElement.GetProperty("sharing").GetProperty("models").GetProperty("exclude")[0].GetString());
            Assert.Contains(status.RootElement.GetProperty("models").EnumerateArray(), m => m.GetProperty("name").GetString() == "qwen3:32b" && !m.GetProperty("shared").GetBoolean());
        }

        // A list left out keeps its value; a name in both is refused before the file is touched.
        var both = await client.PutAsJsonAsync("/api/control/sharing", new { models = new { include = new[] { "qwen3:32b" } } });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("Node:Sharing:Models", await both.Content.ReadAsStringAsync());

        var empty = await client.PutAsJsonAsync("/api/control/sharing", new { models = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var cleared = await client.PutAsJsonAsync("/api/control/sharing", new { models = new { exclude = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        await WaitUntilAsync(() => sharing.IsShared("qwen3:32b"));
        Assert.Null(NodeLocalSettings.Read(Path.Combine(dir, "node.local.json"))["Node"]?["Sharing"]?["Models"]);
    }

    [Fact]
    public async Task AModelTheNodeDoesNotServeIsNeitherSharedNorKept()
    {
        // Found live: with Node:Models narrowing 29 Ollama models to 3, the other 26 all read "shared": true.
        var (host, client, _) = await ControlHostAsync("--Node:Models:Include:0=llama3", "--Node:Models:Include:1=qwen3:32b", "--Node:Sharing:Models:Exclude:0=qwen3:32b");
        await using var owned = host;
        host.Backend.Models = ThreeModels;

        using var models = JsonDocument.Parse(await client.GetStringAsync("/api/control/models"));
        var shared = models.RootElement.GetProperty("models").EnumerateArray()
            .ToDictionary(m => m.GetProperty("name").GetString()!, m => m.TryGetProperty("shared", out var s) ? s.ValueKind : JsonValueKind.Undefined);

        Assert.Equal(JsonValueKind.True, shared["llama3"]);
        Assert.Equal(JsonValueKind.False, shared["qwen3:32b"]);
        // Left out, as a 3.77 node leaves it out: the app shows no box and counts it on neither side.
        Assert.Equal(JsonValueKind.Undefined, shared["nomic-embed-text"]);
    }

    [Fact]
    public async Task AKeptModelStillAnswersOnTheLocalApi()
    {
        var (host, client, _) = await ControlHostAsync("--Node:Sharing:Models:Exclude:0=llama3");
        await using var owned = host;

        Assert.False(host.Services.GetRequiredService<NodeSharing>().IsShared("llama3"));

        using var tags = JsonDocument.Parse(await host.Client.GetStringAsync("/api/tags"));
        Assert.Contains(tags.RootElement.GetProperty("models").EnumerateArray(), m => m.GetProperty("name").GetString() == "llama3");

        var chat = await host.Client.PostAsJsonAsync("/api/chat", new { model = "llama3", messages = new[] { new { role = "user", content = "hi" } }, stream = false });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
    }
}
