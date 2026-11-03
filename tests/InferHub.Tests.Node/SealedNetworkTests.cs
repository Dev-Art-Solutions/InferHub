using System.Net;
using System.Net.Sockets;
using InferHub.Node;
using InferHub.Node.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>Phase 128, the node's half: the egress probe below InferHub's own code, and the rules that match a sealed node.</summary>
public sealed class SealedNetworkTests : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);

    public SealedNetworkTests() => listener.Start();

    private string Open => $"127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";

    private static string Closed()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return $"127.0.0.1:{port}";
    }

    [Fact]
    public async Task AProbeThatConnectsIsAnOpenDoorAndOneThatIsRefusedIsNot()
    {
        Assert.True(await EgressCheck.ConnectsAsync(Open, TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(await EgressCheck.ConnectsAsync(Closed(), TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(await EgressCheck.ConnectsAsync("not-an-address", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.False(await EgressCheck.ConnectsAsync("example.com:443", TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task TheCheckNamesWhatWasReachableAndSaysClosedOnlyWhenSomethingWasTried()
    {
        var closed = Closed();
        var mixed = await Check(Open, closed).CheckAsync(CancellationToken.None);
        Assert.Equal([Open], mixed.Open);
        Assert.Equal([Open, closed], mixed.Probed);
        Assert.False(mixed.Closed);

        Assert.True((await Check(closed).CheckAsync(CancellationToken.None)).Closed);

        var none = await Check().CheckAsync(CancellationToken.None);
        Assert.Empty(none.Probed);
        Assert.False(none.Closed);

        // A name is never tried, so it cannot make the machine look sealed.
        var named = await Check("example.com:443").CheckAsync(CancellationToken.None);
        Assert.Empty(named.Probed);
        Assert.False(named.Closed);
    }

    [Fact]
    public async Task EveryCheckIsAnnounced()
    {
        var check = Check(Open);
        var seen = 0;
        check.Changed += _ => seen++;

        await check.CheckAsync(CancellationToken.None);
        await check.CheckAsync(CancellationToken.None);

        Assert.Equal(2, seen);
        Assert.Equal("node-1", check.Latest!.NodeId);
    }

    [Fact]
    public void WindowsRulesAllowTheCoordinatorAndDnsThenBlockTheRest()
    {
        var rules = SealedFirewall.Rules(SealedFirewall.PowerShell, Targets("https://hub.example.com/", "10.0.0.5"), dns: true, DateTimeOffset.UnixEpoch);

        Assert.Contains("-RemoteAddress '10.0.0.5' -RemotePort 443", rules);
        Assert.Contains("InferHub: DNS (UDP)", rules);
        Assert.EndsWith("Set-NetFirewallProfile -All -DefaultOutboundAction Block" + Environment.NewLine, rules);
        Assert.Contains("Undo:", rules);

        var noDns = SealedFirewall.Rules(SealedFirewall.PowerShell, Targets("https://hub.example.com/", "10.0.0.5"), dns: false, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("DNS (UDP)", noDns);
    }

    [Fact]
    public void NftablesRulesDropByDefaultAndKeepLoopbackAndReplies()
    {
        var rules = SealedFirewall.Rules(
            SealedFirewall.Nftables,
            SealedFirewall.Targets(
                new CoordinatorOptions { Url = "http://hub.lan:5080/", Endpoints = ["http://hub.lan:5080/", "http://[fd00::5]:5080/"] },
                _ => [IPAddress.Parse("192.168.1.10")]),
            dns: false,
            DateTimeOffset.UnixEpoch);

        Assert.Contains("policy drop;", rules);
        Assert.Contains("oif \"lo\" accept", rules);
        Assert.Contains("ct state established,related accept", rules);
        Assert.Contains("ip daddr 192.168.1.10 tcp dport 5080 accept", rules);
        Assert.Contains("ip6 daddr fd00::5 tcp dport 5080 accept", rules);
        Assert.DoesNotContain("dport 53", rules);
        Assert.DoesNotContain("\r", rules);
    }

    [Fact]
    public void AnUnresolvedCoordinatorIsSaidNotSilentlyLeftOut()
    {
        var targets = SealedFirewall.Targets(new CoordinatorOptions { Url = "https://nowhere.invalid/" }, _ => throw new SocketException());

        Assert.Contains("did not resolve", SealedFirewall.Rules(SealedFirewall.Nftables, targets, dns: true, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void TheCommandRefusesAFormatItDoesNotKnow()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Coordinator:Url"] = "http://10.0.0.5:5080/" }).Build();

        Assert.Equal(2, FirewallCommand.Run(["firewall", "pf"], configuration, output, error));
        Assert.Contains("powershell or nftables", error.ToString());
        Assert.Equal(0, FirewallCommand.Run(["firewall", "nftables", "--no-dns"], configuration, output, error));
        Assert.Contains("ip daddr 10.0.0.5 tcp dport 5080", output.ToString());
    }

    private static IReadOnlyList<SealedFirewall.Target> Targets(string url, string address)
        => SealedFirewall.Targets(new CoordinatorOptions { Url = url }, _ => [IPAddress.Parse(address)]);

    private static EgressCheck Check(params string[] probes)
        => new(
            Options.Create(new NodeOptions { Egress = new EgressOptions { Probes = probes.ToList(), TimeoutSeconds = 2 } }),
            new Fixed("node-1"),
            NullLogger<EgressCheck>.Instance);

    public void Dispose() => listener.Stop();

    private sealed class Fixed(string id) : INodeIdentity
    {
        public string GetOrCreateNodeId() => id;
    }
}
