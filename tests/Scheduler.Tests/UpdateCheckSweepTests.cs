namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// The two decisions the update sweep makes on its own. Everything else it does is the engine's:
/// it calls <c>check-update --emit</c> and kgsm decides what is worth announcing.
/// </summary>
public class UpdateCheckSweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    // ---- what counts as recently checked -----------------------------------

    [Fact]
    public void AServerWithNoRecordedCheckIsAlwaysDue()
    {
        Assert.False(UpdateCheckSweep.WasCheckedRecently(null, Now, 60));
    }

    [Theory]
    [InlineData(1)]    // just checked
    [InlineData(29)]   // inside the half-interval window
    public void ARecentCheckIsLeftAlone(int minutesAgo)
    {
        Assert.True(UpdateCheckSweep.WasCheckedRecently(
            Now.AddMinutes(-minutesAgo), Now, intervalMinutes: 60));
    }

    // Half the interval, not the whole of it. A sweep staggers, so the server checked last in the
    // previous sweep is fractionally YOUNGER than one interval when the next sweep begins —
    // measured against the full interval it would skip that sweep, and by the one after it would be
    // two intervals stale.
    [Theory]
    [InlineData(30)]
    [InlineData(59)]
    [InlineData(600)]
    public void ACheckOlderThanHalfTheIntervalIsDueAgain(int minutesAgo)
    {
        Assert.False(UpdateCheckSweep.WasCheckedRecently(
            Now.AddMinutes(-minutesAgo), Now, intervalMinutes: 60));
    }

    // The point of consulting the engine's record at all: the cadence belongs to the interval, not
    // to the daemon's uptime. Restarting the scheduler must not re-ask every upstream for an answer
    // taken a minute ago — a few deploys in a row would otherwise be a burst of steamcmd logins.
    [Fact]
    public void ARestartDoesNotReAskForAnAnswerJustTaken()
    {
        Assert.True(UpdateCheckSweep.WasCheckedRecently(
            Now.AddSeconds(-90), Now, intervalMinutes: 60));
    }

    // A timestamp in the future is not evidence of freshness — a clock moved, or an instance
    // directory was copied in from elsewhere. Trusting it would suppress checks indefinitely.
    [Fact]
    public void ACheckStampedInTheFutureIsNotTreatedAsFresh()
    {
        Assert.False(UpdateCheckSweep.WasCheckedRecently(
            Now.AddHours(1), Now, intervalMinutes: 60));
    }

    // ---- what a failure reports --------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void NoFailureDetailSummarizesToNothing(string? stderr)
    {
        Assert.Null(EngineDetail.Summarize(stderr));
    }

    // kgsm writes several lines of context on a failed check and the status snapshot is one NDJSON
    // line per connection, so the last line — the one naming what actually went wrong — travels.
    [Fact]
    public void TheLastLineOfAMultiLineFailureIsWhatTravels()
    {
        const string stderr = """
            [ERROR] instances.sh:2216 Running update check...
            [ERROR] instances.sh:2219 Could not determine the latest version for 'starbound'
            """;

        Assert.Equal(
            "[ERROR] instances.sh:2219 Could not determine the latest version for 'starbound'",
            EngineDetail.Summarize(stderr));
    }

    [Fact]
    public void ASingleLineFailureIsTrimmed()
    {
        Assert.Equal("registry did not answer", EngineDetail.Summarize("  registry did not answer \n"));
    }
}
