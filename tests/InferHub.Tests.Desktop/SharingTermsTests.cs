using System.Text.Json.Nodes;
using InferHub.Desktop;

namespace InferHub.Tests;

/// <summary>Phase 116 in the app: the owner's hours as typed, and the line the Overview tab shows for the terms.</summary>
public sealed class SharingTermsTests
{
    [Fact]
    public void HoursAreOneWindowPerLineOrSemicolon()
    {
        Assert.Equal(["mon-fri 18:00-08:00", "weekends all day", "sun 10:00-12:00"],
            SharingTerms.Lines("mon-fri 18:00-08:00\r\n\n  weekends all day ; sun 10:00-12:00\n"));
        Assert.Empty(SharingTerms.Lines("  \n "));
    }

    [Fact]
    public void NoTermsSayAnyHourAndNoAllowance()
    {
        var sharing = JsonNode.Parse("""{"mode":"always","hours":[],"allowance":{"minutes":0,"usedSeconds":0}}""");

        Assert.Equal("Shares at any hour · no daily allowance.", SharingTerms.Describe(sharing));
        Assert.Equal("", SharingTerms.HoursText(sharing));
    }

    [Fact]
    public void TermsSayTheHoursTheZoneAndTodaysMinutes()
    {
        var sharing = JsonNode.Parse("""
            {"hours":["mon-fri 18:00-08:00","weekends all day"],"zone":"Europe/Sofia",
             "allowance":{"minutes":120,"usedSeconds":2530.4,"leftSeconds":4669.6,"resetsAtUtc":"2026-10-25T22:00:00+00:00"}}
            """);

        Assert.Equal(
            "Shares mon-fri 18:00-08:00; weekends all day (Europe/Sofia) · today 42 of 120 min of network work used, renewed at 22:00.",
            SharingTerms.Describe(sharing, TimeZoneInfo.Utc));
        Assert.Equal($"mon-fri 18:00-08:00{Environment.NewLine}weekends all day", SharingTerms.HoursText(sharing));
    }
}
