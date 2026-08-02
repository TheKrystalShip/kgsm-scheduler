using TheKrystalShip.Kgsm.Scheduler;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// The scheduler's timing: when a schedule next fires, and when a standing schedule becomes due.
/// These are two different questions, and conflating them is what silently disables the whole
/// daemon — see <see cref="AComputedFireTimeIsAlwaysInTheFuture"/>.
/// </summary>
public class ScheduleTimingTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    // ---- ComputeNextFire ---------------------------------------------------

    [Fact]
    public void DailyFiresAtTheConfiguredTimeLaterToday()
    {
        var now = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);

        var next = ScheduleClock.ComputeNextFire("daily", "04:00", null, Utc, now);

        Assert.Equal(new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void DailyRollsToTomorrowOnceTodaysTimeHasPassed()
    {
        var now = new DateTime(2026, 8, 2, 5, 0, 0, DateTimeKind.Utc);

        var next = ScheduleClock.ComputeNextFire("daily", "04:00", null, Utc, now);

        Assert.Equal(new DateTime(2026, 8, 3, 4, 0, 0, DateTimeKind.Utc), next);
    }

    /// <summary>
    /// The property the engine's design depends on. Because a computed fire time is ALWAYS after
    /// the instant it was computed from, asking "is ComputeNextFire(now) &lt;= now" can never be
    /// true — a scheduler written that way computes a next-fire time forever and never acts on it.
    /// The engine therefore stores the target and compares later instants against it.
    /// </summary>
    [Theory]
    [InlineData("daily")]
    [InlineData("weekly")]
    [InlineData("6h")]
    public void AComputedFireTimeIsAlwaysInTheFuture(string cadence)
    {
        var now = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc);

        // 04:00 is exactly the configured time — the most likely instant to produce "now".
        var next = ScheduleClock.ComputeNextFire(cadence, "04:00", "sun", Utc, now);

        Assert.NotNull(next);
        Assert.True(next > now, $"{cadence} produced a fire time at or before the instant it was computed from");
    }

    [Fact]
    public void WeeklyLandsOnTheConfiguredDay()
    {
        // 2026-08-02 is a Sunday.
        var now = new DateTime(2026, 8, 2, 5, 0, 0, DateTimeKind.Utc);

        var next = ScheduleClock.ComputeNextFire("weekly", "04:00", "wed", Utc, now);

        Assert.NotNull(next);
        Assert.Equal(DayOfWeek.Wednesday, next!.Value.DayOfWeek);
        Assert.Equal(new DateTime(2026, 8, 5, 4, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void SixHourlyIgnoresTheConfiguredTimeOfDay()
    {
        var now = new DateTime(2026, 8, 2, 7, 30, 0, DateTimeKind.Utc);

        // An interval cadence is an interval, not an appointment: the time of day is not consulted.
        var withTime = ScheduleClock.ComputeNextFire("6h", "04:00", null, Utc, now);
        var withOther = ScheduleClock.ComputeNextFire("6h", "23:15", null, Utc, now);

        Assert.Equal(new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc), withTime);
        Assert.Equal(withTime, withOther);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void AnInactiveCadenceNeverFires(string? cadence)
    {
        var next = ScheduleClock.ComputeNextFire(cadence, "04:00", null, Utc, DateTime.UtcNow);

        Assert.Null(next);
    }

    [Theory]
    [InlineData("hourly")]   // not a cadence this scheduler knows
    [InlineData("monthly")]
    public void AnUnknownCadenceYieldsNoFireTimeRatherThanAGuess(string cadence)
    {
        var next = ScheduleClock.ComputeNextFire(cadence, "04:00", null, Utc, DateTime.UtcNow);

        Assert.Null(next);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("04:61")]
    [InlineData("4pm")]
    [InlineData("0400")]
    public void AnUnparseableTimeYieldsNoFireTime(string time)
    {
        var next = ScheduleClock.ComputeNextFire("daily", time, null, Utc, DateTime.UtcNow);

        Assert.Null(next);
    }

    // ---- Plan / IsDue ------------------------------------------------------

    [Fact]
    public void AStandingTargetBecomesDueOnceTheClockReachesIt()
    {
        var scheduled = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc);
        var plan = new SchedulePlan("daily|04:00||UTC", scheduled);

        Assert.False(SchedulerEngine.IsDue(plan, scheduled.AddSeconds(-1)));
        Assert.True(SchedulerEngine.IsDue(plan, scheduled));
        Assert.True(SchedulerEngine.IsDue(plan, scheduled.AddMinutes(3)));
    }

    [Fact]
    public void AnInactiveScheduleIsNeverDue()
    {
        var plan = SchedulerEngine.Plan(null, "off", "04:00", null, Utc, DateTime.UtcNow);

        Assert.Null(plan.NextUtc);
        Assert.False(SchedulerEngine.IsDue(plan, DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public void AnUnchangedScheduleKeepsItsStandingTarget()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        var first = SchedulerEngine.Plan(null, "daily", "04:00", null, Utc, t0);

        // A later tick must not recompute: recomputing from the present is exactly what pushes the
        // target forever out of reach.
        var later = SchedulerEngine.Plan(first, "daily", "04:00", null, Utc, t0.AddMinutes(59));

        Assert.Same(first, later);
        Assert.Equal(new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc), later.NextUtc);
    }

    [Fact]
    public void EditingTheScheduleReplacesTheStandingTarget()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        var first = SchedulerEngine.Plan(null, "daily", "04:00", null, Utc, t0);

        var edited = SchedulerEngine.Plan(first, "daily", "06:00", null, Utc, t0);

        Assert.NotSame(first, edited);
        Assert.Equal(new DateTime(2026, 8, 2, 6, 0, 0, DateTimeKind.Utc), edited.NextUtc);
    }

    [Fact]
    public void ChangingOnlyTheTimezoneReplacesTheStandingTarget()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

        var utcPlan = SchedulerEngine.Plan(null, "daily", "04:00", null, Utc, t0);
        var madridPlan = SchedulerEngine.Plan(utcPlan, "daily", "04:00", null, madrid, t0);

        Assert.NotSame(utcPlan, madridPlan);
        Assert.NotEqual(utcPlan.NextUtc, madridPlan.NextUtc);
    }

    [Fact]
    public void TheRestartAndBackupSchedulesAreIndependent()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);

        var restart = SchedulerEngine.Plan(null, "daily", "04:00", null, Utc, t0);
        var backup = SchedulerEngine.Plan(null, "daily", "05:00", null, Utc, t0);

        // A backup no longer needs a restart window to happen in, so the two carry their own
        // targets and neither gates the other.
        Assert.Equal(new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc), restart.NextUtc);
        Assert.Equal(new DateTime(2026, 8, 2, 5, 0, 0, DateTimeKind.Utc), backup.NextUtc);
    }

    [Fact]
    public void ABackupScheduleRunsWithNoRestartScheduleAtAll()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);

        var restart = SchedulerEngine.Plan(null, "off", "04:00", null, Utc, t0);
        var backup = SchedulerEngine.Plan(null, "6h", null, null, Utc, t0);

        Assert.Null(restart.NextUtc);
        Assert.NotNull(backup.NextUtc);
    }
}
