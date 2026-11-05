using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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

    // ---- what a sweep does -------------------------------------------------

    private static (UpdateCheckSweep Sweep, EngineStub Engine, ScheduleRegistry Registry) NewSweep()
    {
        var engine = new EngineStub();
        engine.Instances["mc-01"] = TestInstances.Config("mc-01");
        engine.Instances["mc-02"] = TestInstances.Config("mc-02");
        var registry = new ScheduleRegistry();
        var options = Options.Create(new SchedulerOptions
        {
            UpdateCheckEnabled = true,
            UpdateCheckIntervalMinutes = 60,
            UpdateCheckStaggerSeconds = 0,
        });

        return (new UpdateCheckSweep(engine, options, registry, NullLogger<UpdateCheckSweep>.Instance), engine, registry);
    }

    [Fact]
    public async Task ASweepAsksEveryServerAndRecordsEachAnswer()
    {
        var (sweep, engine, registry) = NewSweep();

        await sweep.SweepAsync(CancellationToken.None);

        Assert.Equal(["check:mc-01", "check:mc-02"], engine.Calls.Where(c => c.StartsWith("check:", StringComparison.Ordinal)));
        Assert.True(registry.Get("mc-01")!.LastUpdateCheckOk);
        Assert.Null(registry.Get("mc-01")!.LastUpdateCheckMessage);
    }

    [Fact]
    public async Task AFailedCheckRecordsTheEnginesOwnMessageAndTheSweepGoesOn()
    {
        var (sweep, engine, registry) = NewSweep();
        engine.CheckExitCode = 1;

        await sweep.SweepAsync(CancellationToken.None);

        ScheduleState state = registry.Get("mc-02")!;
        Assert.False(state.LastUpdateCheckOk);
        Assert.Equal("the upstream refused", state.LastUpdateCheckMessage);
        Assert.Contains("check:mc-02", engine.Calls);
    }

    [Fact]
    public async Task AServerCheckedRecentlyAccordingToTheEngineIsLeftAlone()
    {
        var (sweep, engine, _) = NewSweep();
        engine.Recorded("mc-01", current: "1.0", latest: "1.0", updatesAvailable: false);

        await sweep.SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("check:mc-01", engine.Calls);
        Assert.Contains("check:mc-02", engine.Calls);
    }

    [Fact]
    public async Task AnUnreadableRegistryChecksNothing()
    {
        var (sweep, engine, _) = NewSweep();
        engine.RegistryReadable = false;

        await sweep.SweepAsync(CancellationToken.None);

        Assert.Empty(engine.Calls);
    }
}
