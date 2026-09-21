using InferHub.Node.LocalApi;
using InferHub.Node.Resources;

namespace InferHub.Tests;

/// <summary>Phase 82, D4. The solo-mode half of enforcement, against a scripted governor.</summary>
public class ResourceAdmissionGateTests
{
    private sealed class FakeGovernor : IResourceGovernor
    {
        public ResourceSnapshot Current { get; init; }

        public bool IsThrottled { get; init; }

        public string? Reason { get; init; }

        public event Action? Changed { add { } remove { } }
    }

    [Fact]
    public void EntersFreelyWhenNotThrottled()
    {
        var gate = new ResourceAdmissionGate(new FakeGovernor { IsThrottled = false });

        Assert.True(gate.TryEnter(out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void RefusesWithTheGovernorsOwnReasonWhenThrottled()
    {
        var gate = new ResourceAdmissionGate(new FakeGovernor { IsThrottled = true, Reason = "CPU 95% > Node:ResourceLimits:MaxCpuPercent (80%)" });

        Assert.False(gate.TryEnter(out var reason));
        Assert.Equal("CPU 95% > Node:ResourceLimits:MaxCpuPercent (80%)", reason);
    }

    [Fact]
    public void FallsBackToAGenericReasonWhenTheGovernorGivesNone()
    {
        var gate = new ResourceAdmissionGate(new FakeGovernor { IsThrottled = true, Reason = null });

        Assert.False(gate.TryEnter(out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }
}
