using System.Text.Json;
using InferHub.Coordinator.Services;
using InferHub.Shared.Fit;

namespace InferHub.Tests;

/// <summary>
/// Phase 133 (D1, D4): one node reads the model, the rest are asked only for their machines, each answers by its own
/// owner's terms, and a node that cannot answer is listed with its reason rather than guessed at.
/// </summary>
public sealed class GroupFitServiceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly ModelFacts Seventy = new(
        ModelFacts.SourceGguf, "Llama-70B", "llama", "text-generation", 70_000_000_000, 80, 80, 8 * 256, 131072, null, null, 0,
        [new ModelVariant("Q4_K_M", 42_520_000_000)], []);

    private static MachineFacts Card(long mib) =>
        new([new GpuFacts(0, "card", mib, mib)], 8192, 8192, 16, [new EngineFacts(FitEngines.LlamaCpp, null, true, null, "/m", 1 << 21, null, null)], []);

    private static NodeManagementControl.Answer Ok(object payload) => new(200, JsonSerializer.Serialize(payload, Json), null);

    private static readonly NodeManagementControl.Answer NotAllowed =
        new(403, null, "this node's owner has not let its coordinator manage its models (Node:Management:AllowFromHub is off)");

    [Fact]
    public async Task OneNodeReadsTheModelTheOthersGiveTheirMachinesAndEachRefusalIsNamed()
    {
        var calls = new List<(string Node, string Operation)>();

        Task<NodeManagementControl.Answer> Ask(string node, string operation, ManagementRequest request, CancellationToken _)
        {
            lock (calls) calls.Add((node, operation));
            return Task.FromResult((node, operation) switch
            {
                ("a", _) => NotAllowed,
                ("b", ManagementJob.Estimate) => Ok(new { link = "owner/Llama-70B-GGUF", context = request.Context, model = Seventy, machine = Card(16384) }),
                ("c" or "e", ManagementJob.Machine) => Ok(Card(16384)),
                ("d", ManagementJob.Machine) => new NodeManagementControl.Answer(400, null, "'machine' is not a management operation; this node knows catalog, estimate, install"),
                _ => throw new InvalidOperationException($"{node} was asked {operation}")
            });
        }

        var nodes = new[] { "a", "b", "c", "d", "e" }.Select(n => new GroupFitService.Candidate(n, n.ToUpperInvariant())).ToArray();
        var answer = await GroupFitService.ComposeAsync(nodes, Ask, "owner/Llama-70B-GGUF", 8192, CancellationToken.None);

        Assert.Equal(200, answer.Status);
        Assert.Equal("b", answer.ReadBy);
        Assert.Equal(80, answer.Layers);
        Assert.Null(answer.Refused);

        // The model was read once; nobody but the reader read it, and the reader was not asked for its machine again.
        Assert.Equal(2, calls.Count(c => c.Operation == ManagementJob.Estimate));   // a refused, then b
        Assert.DoesNotContain(("b", ManagementJob.Machine), calls);

        Assert.Contains("AllowFromHub", answer.Asked!.Single(a => a.NodeId == "a").Why);
        Assert.Contains("older than 3.93", answer.Asked!.Single(a => a.NodeId == "d").Why);
        Assert.All(answer.Asked!.Where(a => a.NodeId is "b" or "c" or "e"), a => Assert.True(a.Ok));

        var plan = Assert.Single(answer.Plans!);
        Assert.True(plan.Fits);
        Assert.Equal(["b", "c", "e"], plan.Members.Select(m => m.NodeId).Order());
    }

    [Fact]
    public async Task ALinkTheNodeCallsBadIsTheLinksFaultNotTheNodes()
    {
        var answer = await GroupFitService.ComposeAsync(
            [new GroupFitService.Candidate("a", "A"), new GroupFitService.Candidate("b", "B")],
            (_, _, _, _) => Task.FromResult(new NodeManagementControl.Answer(400, null, "'!!' is not a link this node reads")),
            "!!", 8192, CancellationToken.None);

        Assert.Equal(400, answer.Status);
        Assert.Contains("not a link", answer.Error);
    }

    [Fact]
    public async Task NobodyAbleToReadTheModelIsAConflictWithEveryReason()
    {
        var answer = await GroupFitService.ComposeAsync(
            [new GroupFitService.Candidate("a", "A")],
            (_, _, _, _) => Task.FromResult(NotAllowed),
            "owner/x", null, CancellationToken.None);

        Assert.Equal(409, answer.Status);
        Assert.Contains("AllowFromHub", answer.Asked!.Single().Why);
    }

    [Fact]
    public async Task ACheckpointIsReadButRefusedWithTheSentenceAndNoPlans()
    {
        var checkpoint = Seventy with { Source = ModelFacts.SourceCheckpoint };
        var answer = await GroupFitService.ComposeAsync(
            [new GroupFitService.Candidate("a", "A")],
            (_, _, _, _) => Task.FromResult(Ok(new { link = "owner/x", model = checkpoint, machine = Card(16384) })),
            "owner/x", 8192, CancellationToken.None);

        Assert.Equal(200, answer.Status);
        Assert.Contains("GGUF", answer.Refused);
        Assert.Empty(answer.Plans!);
    }
}
