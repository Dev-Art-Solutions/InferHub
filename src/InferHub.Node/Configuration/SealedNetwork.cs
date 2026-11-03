using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace InferHub.Node.Configuration;

/// <summary>
/// Phase 128. <c>Node:Egress:</c> — how a sealed node checks that the machine, not only InferHub, reaches nothing outside.
/// Read only while <c>Node:Sealed</c>.
/// </summary>
public sealed class EgressOptions
{
    /// <summary>
    /// Addresses outside every private network, tried with a bare TCP connect (no byte sent). An empty list turns the
    /// check off. IP literals on purpose: a name would need DNS, and a resolver allowed through the firewall would
    /// make a closed machine look open.
    /// </summary>
    public IList<string> Probes { get; set; } = ["1.1.1.1:443", "8.8.8.8:443", "9.9.9.9:443"];

    /// <summary>How often to check again. 0: only at start.</summary>
    public int CheckMinutes { get; set; } = 15;

    public int TimeoutSeconds { get; set; } = 3;
}

/// <summary>
/// Phase 128 D1. Tries each probe below the <see cref="SealedEgressHandler"/>: a raw socket, so what it measures is the
/// operating system's firewall, the one layer InferHub's code cannot vouch for. A probe that connects is the machine's
/// door left open; the node says so once in its log, to its owner (<c>/api/control/egress</c>) and to its coordinator.
/// </summary>
public sealed class EgressCheck(
    IOptions<NodeOptions> options,
    INodeIdentity identity,
    ILogger<EgressCheck> logger,
    TimeProvider? clock = null) : BackgroundService
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private string? lastVerdict;

    public NodeEgressState? Latest { get; private set; }

    public event Action<NodeEgressState>? Changed;

    public async Task<NodeEgressState> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Egress;
        // D1: IP:port only. A name is left out of the check rather than counted as refused — a door that was never tried
        // must not make the machine look sealed.
        var probes = settings.Probes
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(p => IPEndPoint.TryParse(p, out var endpoint) && endpoint.Port != 0)
            .ToArray();
        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 30));
        var results = await Task.WhenAll(probes.Select(probe => ConnectsAsync(probe, timeout, cancellationToken)));
        var open = probes.Where((_, i) => results[i]).ToArray();
        var state = new NodeEgressState(identity.GetOrCreateNodeId(), time.GetUtcNow(), probes, open);
        var verdict = string.Join(",", open);

        if (verdict != lastVerdict)
        {
            lastVerdict = verdict;

            if (open.Length > 0)
            {
                logger.LogWarning(
                    "Node:Sealed — this machine can reach {Open} itself: InferHub does not, but the operating system lets anything else on it. "
                    + "`InferHub.Node firewall` prints rules that allow only the coordinator",
                    string.Join(", ", open));
            }
            else if (probes.Length > 0)
            {
                logger.LogInformation("Node:Sealed — the operating system refused all {Count} egress probes: this machine reaches nothing outside", probes.Length);
            }
        }

        Latest = state;
        Changed?.Invoke(state);
        return state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Egress.Probes.Count == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Egress check failed to run");
            }

            if (options.Value.Egress.CheckMinutes <= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.Egress.CheckMinutes), stoppingToken);
        }
    }

    internal static async Task<bool> ConnectsAsync(string probe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!IPEndPoint.TryParse(probe, out var endpoint) || endpoint.Port == 0)
        {
            return false;
        }

        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);

        try
        {
            await socket.ConnectAsync(endpoint, bounded.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>
/// Phase 128 D2. Firewall rules that match a sealed node: outbound only to its coordinators (and, unless asked not to, to
/// DNS), loopback and replies kept, everything else dropped. Printed, never applied: they change the whole machine's
/// network, and only its owner may decide that (D3).
/// </summary>
public static class SealedFirewall
{
    public const string PowerShell = "powershell";
    public const string Nftables = "nftables";

    public sealed record Target(string Host, IReadOnlyList<IPAddress> Addresses, int Port);

    public static IReadOnlyList<Target> Targets(CoordinatorOptions coordinator, Func<string, IPAddress[]>? resolve = null)
    {
        resolve ??= Dns.GetHostAddresses;
        var targets = new List<Target>();

        foreach (var url in coordinator.ResolvedEndpoints())
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            IPAddress[] addresses;

            try
            {
                addresses = IPAddress.TryParse(uri.Host, out var literal) ? [literal] : resolve(uri.Host);
            }
            catch (SocketException)
            {
                addresses = [];
            }

            targets.Add(new Target(uri.Host, addresses.Distinct().ToArray(), uri.Port));
        }

        return targets;
    }

    public static string Rules(string format, IReadOnlyList<Target> targets, bool dns, DateTimeOffset now)
        => format.Trim().ToLowerInvariant() switch
        {
            PowerShell => ForWindows(targets, dns, now),
            Nftables => ForNftables(targets, dns, now),
            _ => throw new ArgumentException($"'{format}' is not a format; use {PowerShell} or {Nftables}")
        };

    private static string ForWindows(IReadOnlyList<Target> targets, bool dns, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Header(text, "#", targets, now);
        text.AppendLine("# Windows Defender Firewall, PowerShell as Administrator. This blocks outbound traffic for the WHOLE machine.");
        text.AppendLine("# Undo: Set-NetFirewallProfile -All -DefaultOutboundAction Allow; Remove-NetFirewallRule -Group 'InferHub sealed node'");
        text.AppendLine("$ErrorActionPreference = 'Stop'");
        text.AppendLine("Remove-NetFirewallRule -Group 'InferHub sealed node' -ErrorAction SilentlyContinue");

        foreach (var target in targets)
        {
            if (target.Addresses.Count == 0)
            {
                text.AppendLine($"# {target.Host}: did not resolve when this was generated; add its address by hand or the node cannot connect");
                continue;
            }

            var addresses = string.Join(",", target.Addresses.Select(a => $"'{a}'"));
            text.AppendLine(
                $"New-NetFirewallRule -Group 'InferHub sealed node' -DisplayName 'InferHub: coordinator {Quote(target.Host)}:{target.Port}' "
                + $"-Direction Outbound -Action Allow -Protocol TCP -RemoteAddress {addresses} -RemotePort {target.Port} | Out-Null");
        }

        if (dns)
        {
            text.AppendLine("# DNS, so the node can resolve its coordinator's name. Use --no-dns with an IP or a hosts entry instead.");
            text.AppendLine("$resolvers = (Get-DnsClientServerAddress -AddressFamily IPv4,IPv6).ServerAddresses | Sort-Object -Unique");
            text.AppendLine("if ($resolvers) {");
            text.AppendLine("  New-NetFirewallRule -Group 'InferHub sealed node' -DisplayName 'InferHub: DNS (UDP)' -Direction Outbound -Action Allow -Protocol UDP -RemoteAddress $resolvers -RemotePort 53 | Out-Null");
            text.AppendLine("  New-NetFirewallRule -Group 'InferHub sealed node' -DisplayName 'InferHub: DNS (TCP)' -Direction Outbound -Action Allow -Protocol TCP -RemoteAddress $resolvers -RemotePort 53 | Out-Null");
            text.AppendLine("}");
        }

        text.AppendLine("# Loopback is never filtered by Windows Defender Firewall: the node's engines on 127.0.0.1 keep working.");
        text.AppendLine("Set-NetFirewallProfile -All -DefaultOutboundAction Block");
        return text.ToString();
    }

    private static string ForNftables(IReadOnlyList<Target> targets, bool dns, DateTimeOffset now)
    {
        var text = new StringBuilder();
        Header(text, "#", targets, now);
        text.AppendLine("# nftables, as root: nft -f <this file>. This drops outbound traffic for the WHOLE machine.");
        text.AppendLine("# Undo: nft delete table inet inferhub_sealed");
        text.AppendLine("table inet inferhub_sealed");
        text.AppendLine("delete table inet inferhub_sealed");
        text.AppendLine("table inet inferhub_sealed {");
        text.AppendLine("  chain output {");
        text.AppendLine("    type filter hook output priority 0; policy drop;");
        text.AppendLine("    oif \"lo\" accept");
        text.AppendLine("    ct state established,related accept");

        foreach (var target in targets)
        {
            if (target.Addresses.Count == 0)
            {
                text.AppendLine($"    # {target.Host}: did not resolve when this was generated; add its address by hand");
                continue;
            }

            foreach (var address in target.Addresses)
            {
                var family = address.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip";
                text.AppendLine($"    {family} daddr {address} tcp dport {target.Port} accept comment \"coordinator {target.Host}\"");
            }
        }

        if (dns)
        {
            text.AppendLine("    # DNS, so the node can resolve its coordinator's name. Narrow it to your resolvers, or use --no-dns.");
            text.AppendLine("    udp dport 53 accept");
            text.AppendLine("    tcp dport 53 accept");
        }

        text.AppendLine("  }");
        text.AppendLine("}");
        // nft reads a CR as junk at the end of every line: LF always, whatever OS generated the file (found by `nft -c` on rules
        // a Windows node printed).
        return text.ToString().Replace("\r\n", "\n");
    }

    private static void Header(StringBuilder text, string comment, IReadOnlyList<Target> targets, DateTimeOffset now)
    {
        text.AppendLine($"{comment} InferHub sealed node: outbound only to its coordinator(s). Generated {now.ToString("u", CultureInfo.InvariantCulture)}.");
        text.AppendLine($"{comment} Coordinators: {string.Join(", ", targets.Select(t => $"{t.Host}:{t.Port} -> {(t.Addresses.Count == 0 ? "unresolved" : string.Join(" ", t.Addresses))}"))}");
        text.AppendLine($"{comment} Addresses were resolved now; regenerate if a coordinator's address changes.");
    }

    private static string Quote(string text) => text.Replace("'", "''");
}

/// <summary>Phase 128: <c>InferHub.Node firewall [powershell|nftables] [--no-dns]</c> prints the rules and exits.</summary>
public static class FirewallCommand
{
    public static bool Handles(string[] args) => args.Length > 0 && string.Equals(args[0], "firewall", StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, IConfiguration configuration, TextWriter output, TextWriter error)
    {
        var format = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? (OperatingSystem.IsWindows() ? SealedFirewall.PowerShell : SealedFirewall.Nftables);
        var dns = !args.Contains("--no-dns", StringComparer.OrdinalIgnoreCase);
        var coordinator = configuration.GetSection(CoordinatorOptions.SectionName).Get<CoordinatorOptions>() ?? new CoordinatorOptions();

        try
        {
            output.Write(SealedFirewall.Rules(format, SealedFirewall.Targets(coordinator), dns, DateTimeOffset.UtcNow));
            return 0;
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return 2;
        }
    }
}
