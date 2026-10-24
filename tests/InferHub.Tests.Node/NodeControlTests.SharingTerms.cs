using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InferHub.Node.Configuration;
using InferHub.Node.Control;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InferHub.Tests;

/// <summary>Phase 116: the owner's terms — sharing hours and a daily network allowance.</summary>
public sealed partial class NodeControlTests
{
    // Monday 26 October 2026, in UTC so the table reads the same on every machine.
    private static readonly DateTimeOffset Monday = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("mon-fri 18:00-08:00")]
    [InlineData("sat,sun all day")]
    [InlineData("daily 22:00-07:00")]
    [InlineData("weekdays 9:00-17:30")]
    [InlineData("fri-mon 00:00-24:00")]
    [InlineData("Wednesday 12:00-13:00")]
    public void HoursAreLinesAnOwnerCanWrite(string line)
    {
        SharingHours.Parse([line], "UTC", out var error);

        Assert.Null(error);
    }

    [Theory]
    [InlineData("18:00-08:00", "days, then hours")]
    [InlineData("someday 18:00-08:00", "not a day")]
    [InlineData("mon 18:00", "HH:mm-HH:mm")]
    [InlineData("mon 25:00-26:00", "HH:mm-HH:mm")]
    [InlineData("mon 10:00-10:00", "all day")]
    public void HoursThatDoNotParseSayWhy(string line, string expected)
    {
        var hours = SharingHours.Parse([line], "UTC", out var error);

        Assert.Contains(expected, error);
        Assert.True(hours.IsAlways);
    }

    [Fact]
    public void AnUnknownTimeZoneIsRefused()
    {
        SharingHours.Parse(["daily all day"], "Mars/Olympus_Mons", out var error);

        Assert.Contains("not a time zone", error);
    }

    [Theory]
    // mon-fri 18:00-08:00: a window belongs to the day it starts on, and Friday's runs into Saturday morning.
    [InlineData("mon-fri 18:00-08:00", 0, 17, 59, false, 0, 18, 0)]
    [InlineData("mon-fri 18:00-08:00", 1, 7, 59, true, 1, 8, 0)]
    [InlineData("mon-fri 18:00-08:00", 5, 7, 0, true, 5, 8, 0)]
    [InlineData("mon-fri 18:00-08:00", 5, 10, 0, false, 7, 18, 0)]
    // Sunday night into Monday: the window starting the day before is still open.
    [InlineData("sun 22:00-06:00", 0, 3, 0, true, 0, 6, 0)]
    public void TheHoursAreOpenOrClosedAndSayWhenThatChanges(string line, int day, int hour, int minute, bool open, int changeDay, int changeHour, int changeMinute)
    {
        var hours = SharingHours.Parse([line], "UTC", out _);
        var at = Monday.AddDays(day).AddHours(hour).AddMinutes(minute);

        Assert.Equal(open, hours.IsOpen(at, out var changesAt));
        Assert.Equal(Monday.AddDays(changeDay).AddHours(changeHour).AddMinutes(changeMinute), changesAt);
    }

    [Fact]
    public void WindowsThatTouchAreOneAndNeverCloseAtMidnight()
    {
        var hours = SharingHours.Parse(["daily 22:00-24:00", "daily 00:00-07:00"], "UTC", out _);

        Assert.True(hours.IsOpen(Monday.AddHours(23).AddMinutes(59), out var changesAt));
        Assert.Equal(Monday.AddDays(1).AddHours(7), changesAt);
    }

    [Fact]
    public void HoursCoveringTheWholeWeekNeverChange()
    {
        var hours = SharingHours.Parse(["daily all day"], "UTC", out _);

        Assert.True(hours.IsOpen(Monday, out var changesAt));
        Assert.Null(changesAt);
    }

    [Fact]
    public void AStartTheClockSkipsOpensAtTheFirstMinuteItShows()
    {
        // Sofia springs forward on Sunday 28 March 2027: 03:00 EET is 04:00 EEST, so 03:30 never happens.
        var hours = SharingHours.Parse(["sun 03:30-05:00"], "Europe/Sofia", out var error);
        Assert.Null(error);

        Assert.False(hours.IsOpen(new DateTimeOffset(2027, 3, 28, 0, 50, 0, TimeSpan.Zero), out var opens));
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero), opens);
        Assert.True(hours.IsOpen(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero), out var closes));
        Assert.Equal(new DateTimeOffset(2027, 3, 28, 2, 0, 0, TimeSpan.Zero), closes);
    }

    private static (NodeSharing Sharing, NodeActivity Activity, Monitor Options, ManualTime Time) Terms(SharingOptions sharing, DateTimeOffset start, string? usageFile = null)
    {
        sharing.TimeZone ??= "UTC";
        var options = new Monitor(new NodeOptions { Sharing = sharing });
        var time = new ManualTime(start);
        var activity = new NodeActivity(time);
        return (new NodeSharing(options, activity, NullLogger<NodeSharing>.Instance, time, usageFile), activity, options, time);
    }

    [Fact]
    public void OutsideItsHoursANodeTakesNoNetworkJobsAndSaysWhenItWill()
    {
        var (sharing, _, _, time) = Terms(new SharingOptions { Hours = ["mon-fri 18:00-08:00"] }, Monday.AddHours(17).AddMinutes(30));

        Assert.True(sharing.Paused);
        var state = sharing.State("n");
        Assert.StartsWith("outside its sharing hours; shares again today 18:00", state.Reason);
        Assert.Equal(Monday.AddHours(18), state.ResumesAtUtc);

        time.Advance(TimeSpan.FromMinutes(31));

        Assert.False(sharing.Paused);
        Assert.Null(sharing.State("n").ResumesAtUtc);
    }

    [Fact]
    public void PausedIsPausedWhateverTheHour()
    {
        var (sharing, _, _, _) = Terms(new SharingOptions { Mode = "paused", Hours = ["daily all day"] }, Monday);

        Assert.Equal("paused by its owner", sharing.State("n").Reason);
    }

    [Fact]
    public void TheAllowanceRunsOutWhileANetworkJobRunsAndTheJobFinishes()
    {
        var (sharing, activity, _, time) = Terms(new SharingOptions { DailyNetworkMinutes = 1 }, Monday.AddHours(12));

        using (NodeActivity.FromMesh())
        using (activity.Begin("chat", "m"))
        {
            Assert.False(sharing.Paused);
            time.Advance(TimeSpan.FromSeconds(61));

            // 116 D3: nothing is cancelled — only the next job waits.
            Assert.True(sharing.Paused);
            Assert.Equal(1, activity.RunningCount(NodeActivity.Mesh));
        }

        var state = sharing.State("n");
        Assert.StartsWith("today's network allowance of 1 min is used; shares again tomorrow 00:00", state.Reason);
        Assert.Equal(Monday.AddDays(1), state.ResumesAtUtc);
        Assert.Equal(0, sharing.Allowance().LeftSeconds);

        time.Advance(TimeSpan.FromHours(12));

        Assert.False(sharing.Paused);
        Assert.Equal(60, sharing.Allowance().LeftSeconds);
    }

    [Fact]
    public void TwoNetworkJobsAtOnceSpendTheAllowanceTwiceAsFast()
    {
        var (sharing, activity, _, time) = Terms(new SharingOptions { DailyNetworkMinutes = 2 }, Monday.AddHours(12));

        using (NodeActivity.FromMesh())
        using (activity.Begin("chat", "a"))
        using (activity.Begin("chat", "b"))
        {
            time.Advance(TimeSpan.FromSeconds(59));
            Assert.False(sharing.Paused);
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.True(sharing.Paused);
        }
    }

    [Fact]
    public void YourOwnWorkDoesNotSpendTheNetworksAllowance()
    {
        var (sharing, activity, _, time) = Terms(new SharingOptions { DailyNetworkMinutes = 1 }, Monday.AddHours(12));

        using (activity.Begin("chat", "m"))
        {
            time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.False(sharing.Paused);
        Assert.Equal(0, sharing.Allowance().UsedSeconds);
    }

    [Fact]
    public void ARestartIsNotASecondDay()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("inferhub-sharing-").FullName, NodeSharing.UsageFileName);
        var (first, activity, _, time) = Terms(new SharingOptions { DailyNetworkMinutes = 1 }, Monday.AddHours(12), file);

        using (NodeActivity.FromMesh())
        using (activity.Begin("chat", "m"))
        {
            time.Advance(TimeSpan.FromSeconds(45));
        }

        first.Dispose();
        Assert.True(File.Exists(file));

        // The same day: 45 s already used.
        var (again, _, _, _) = Terms(new SharingOptions { DailyNetworkMinutes = 1 }, Monday.AddHours(13), file);
        Assert.Equal(45, again.Allowance().UsedSeconds);

        // The next day: a fresh allowance.
        var (tomorrow, _, _, _) = Terms(new SharingOptions { DailyNetworkMinutes = 1 }, Monday.AddDays(1).AddHours(1), file);
        Assert.Equal(0, tomorrow.Allowance().UsedSeconds);
    }

    [Fact]
    public void HoursThatDoNotParseFailStartup()
    {
        var result = new NodeOptionsValidator().Validate(null, new NodeOptions { Name = "n", Sharing = new SharingOptions { Hours = ["tonight"] } });

        Assert.False(result.Succeeded);
        Assert.Contains("Node:Sharing:Hours", result.FailureMessage);
    }

    [Fact]
    public async Task TheTermsAreSetLiveThroughTheSharingRouteAndShownInTheStatus()
    {
        var (host, client, _) = await ControlHostAsync();
        await using var owned = host;

        var set = await client.PutAsJsonAsync("/api/control/sharing", new { hours = new[] { "daily all day" }, timeZone = "UTC", dailyNetworkMinutes = 90 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var sharing = host.Services.GetRequiredService<NodeSharing>();
        await WaitUntilAsync(() => sharing.Allowance().Minutes == 90);

        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/control/status"));
        var block = status.RootElement.GetProperty("sharing");
        Assert.Equal("daily all day", block.GetProperty("hours")[0].GetString());
        Assert.Equal("UTC", block.GetProperty("timeZone").GetString());
        Assert.Equal(90, block.GetProperty("allowance").GetProperty("minutes").GetInt32());
        Assert.Equal("always", block.GetProperty("mode").GetString());

        var refused = await client.PutAsJsonAsync("/api/control/sharing", new { hours = new[] { "at night" } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Sharing:Hours", await refused.Content.ReadAsStringAsync());

        var cleared = await client.PutAsJsonAsync("/api/control/sharing", new { hours = Array.Empty<string>(), dailyNetworkMinutes = 0 });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        await WaitUntilAsync(() => sharing.Allowance().Minutes == 0 && sharing.Hours.IsAlways);
    }
}
