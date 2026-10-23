using System.Diagnostics;
using InferHub.Node.Configuration;
using InferHub.Node.Resources.Limits;
using InferHub.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InferHub.Tests;

/// <summary>
/// Phase 102 against real processes: a limit the OS holds is one a process cannot get past, and a
/// limit the watchdog holds ends the process that went over. The scripted suite in the Node project
/// proves the decisions; this proves the Job Object, the affinity call and the watchdog do something.
/// </summary>
public class EngineLimitProcessTests
{
    [WindowsFact]
    public async Task OnWindowsAJobObjectMemoryLimitStopsAnAllocationPastIt()
    {
        using var limiter = Limiter(new EngineLimitOptions { MemoryMiB = 150 });

        var refused = await RunAsync(limiter, Allocate(400));

        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("memory", refused.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allocated", refused.Output);
    }

    [WindowsFact]
    public async Task OnWindowsTheSameAllocationUnderAHigherLimitRuns()
    {
        using var limiter = Limiter(new EngineLimitOptions { MemoryMiB = 2048 });

        var allowed = await RunAsync(limiter, Allocate(400));

        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal("os (Job Object memory limit)", limiter.State("n").Groups.Single().Enforcement[LimitResources.Memory]);
    }

    [WindowsFact]
    public async Task OnWindowsAJobObjectCpuCapHoldsABusyProcessDown()
    {
        // One percent of the whole machine: a single busy thread gets a fraction of one core.
        using var limiter = Limiter(new EngineLimitOptions { CpuPercent = 1 });

        var info = Shell("$end = (Get-Date).AddSeconds(3); while ((Get-Date) -lt $end) { }");
        limiter.Prepare(EngineLimitGroups.Tools, info);
        using var process = Process.Start(info)!;
        limiter.Attach(EngineLimitGroups.Tools, process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        // Unlimited, the loop burns about three CPU-seconds. Under the cap it gets a sliver of that.
        var share = Environment.ProcessorCount * 0.01 * 3;
        Assert.True(
            process.TotalProcessorTime < TimeSpan.FromSeconds(Math.Max(1.5, share * 3)),
            $"the capped loop used {process.TotalProcessorTime.TotalSeconds:F2} CPU-seconds");
    }

    [WindowsFact]
    public async Task OnWindowsACoreLimitOffTheJobIsTheProcessAffinityAndAChildInheritsIt()
    {
        // Phase 111: Strata's core limit is held as process affinity, not the job's (see StrataLimitTeller.CoresOnTheJob).
        using var container = WindowsJobContainer.TryCreate("strata", NullLogger.Instance, coresOnTheJob: false)!;
        // The process prints its own affinity after the limit is applied, then starts a child that prints its own.
        var info = Shell("Start-Sleep -Seconds 2; [Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity; & powershell -NoProfile -Command '[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity'");
        using var process = Process.Start(info)!;
        container.Add(process);

        var held = container.Apply(new EngineLimits(Cores: 2));
        var output = await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("os (process affinity, re-applied each poll)", held[LimitResources.Cores]);
        Assert.Equal(["3", "3"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [LinuxFact]
    public async Task OnLinuxAffinityPinsEveryThreadToTheFirstCores()
    {
        using var limiter = Limiter(new EngineLimitOptions { Cores = 1 });

        var info = new ProcessStartInfo("sleep", "5") { UseShellExecute = false };
        using var process = Process.Start(info)!;
        limiter.Attach(EngineLimitGroups.Tools, process);
        limiter.Poll();

        var status = await File.ReadAllLinesAsync($"/proc/{process.Id}/status");
        Assert.Contains("Cpus_allowed_list:\t0", status);
        process.Kill();
    }

    [LinuxFact]
    public async Task OnLinuxWithoutACgroupTheWatchdogEndsAProcessOverItsMemory()
    {
        if (!File.Exists("/usr/bin/python3"))
        {
            return;
        }

        using var limiter = Limiter(new EngineLimitOptions { MemoryMiB = 100 });

        var info = new ProcessStartInfo("/usr/bin/python3", ["-c", "a = bytearray(400 * 1024 * 1024)\nimport time\ntime.sleep(30)"]) { UseShellExecute = false };
        using var process = Process.Start(info)!;
        limiter.Attach(EngineLimitGroups.Tools, process);

        for (var i = 0; i < 100 && !process.HasExited; i++)
        {
            await Task.Delay(100);
            limiter.Poll();
        }

        Assert.True(process.HasExited, "the process over its memory limit is still running");

        var row = limiter.State("n").Groups.Single();

        // A box with a delegated cgroup kills it in the kernel instead; either way it is gone.
        if (!row.Enforcement[LimitResources.Memory].StartsWith("os", StringComparison.Ordinal))
        {
            Assert.Equal(1, row.Breaches);
        }
    }

    private static EngineLimiter Limiter(EngineLimitOptions tools)
    {
        var options = new NodeOptions();
        options.ResourceLimits.Engines[EngineLimitGroups.Tools] = tools;
        options.ResourceLimits.PollInterval = TimeSpan.FromMilliseconds(100);
        return new EngineLimiter(new Monitor(options), TimeProvider.System, NullLogger<EngineLimiter>.Instance);
    }

    private static ProcessStartInfo Allocate(int mib) =>
        Shell($"$ErrorActionPreference = 'Stop'; $a = New-Object byte[] ({mib}MB); $a[0] = 1; 'allocated'");

    private static ProcessStartInfo Shell(string script)
    {
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(EngineLimiter limiter, ProcessStartInfo info)
    {
        limiter.Prepare(EngineLimitGroups.Tools, info);
        using var process = Process.Start(info)!;
        limiter.Attach(EngineLimitGroups.Tools, process);

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        return (process.ExitCode, await stdout + await stderr);
    }

    private sealed class Monitor(NodeOptions value) : IOptionsMonitor<NodeOptions>
    {
        public NodeOptions CurrentValue => value;

        public NodeOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<NodeOptions, string?> listener) => null;
    }
}

/// <summary>Runs only on Windows, where the Job Object is; skipped visibly elsewhere.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only: Job Objects";
        }
    }
}

/// <summary>Runs only on Linux; skipped visibly elsewhere.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only: /proc and sched_setaffinity";
        }
    }
}
