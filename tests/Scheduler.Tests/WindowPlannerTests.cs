using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// What this daemon decides about a window on top of what kgsm-lib read: whether it will fire it,
/// when its standing target becomes due, and how late a fire may be.
/// </summary>
public class WindowPlannerTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static readonly MaintenanceTaskCatalog Catalog = new([
        new BackupTask(NullLogger<BackupTask>.Instance),
        new UpdateTask(Options.Create(new SchedulerOptions()), NullLogger<UpdateTask>.Instance),
        new RestartTask(NullLogger<RestartTask>.Instance),
    ]);

    private static readonly DateTime T0 = new(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);

    private static ReadWindow Read(string expression, int minimumPeriodMinutes = 10) =>
        WindowPlanner.Read(
            MaintenanceWindowParser.ParseWindow(expression), Catalog, minimumPeriodMinutes, Utc, T0);

    // ---- the standing target -----------------------------------------------

    /// <summary>
    /// The property the engine's design depends on. A computed fire time is ALWAYS after the instant
    /// it was computed from, so asking "is the next fire &lt;= now" can never be true — a scheduler
    /// written that way computes a target forever and never acts on it. The engine therefore stores
    /// the target and compares later instants against it.
    /// </summary>
    [Fact]
    public void A_standing_target_becomes_due_once_the_clock_reaches_it()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        WindowPlan plan = WindowPlanner.Plan(null, Read("daily@04:00/restart"), Utc, t0);

        var scheduled = new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc);
        Assert.Equal(scheduled, plan.NextUtc);
        Assert.False(WindowPlanner.IsDue(plan, scheduled.AddSeconds(-1)));
        Assert.True(WindowPlanner.IsDue(plan, scheduled));
        Assert.True(WindowPlanner.IsDue(plan, scheduled.AddMinutes(3)));
    }

    [Fact]
    public void An_unchanged_window_keeps_its_standing_target()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        WindowPlan first = WindowPlanner.Plan(null, Read("daily@04:00/restart"), Utc, t0);

        // A later tick must not recompute: recomputing from the present is exactly what pushes the
        // target forever out of reach.
        WindowPlan later = WindowPlanner.Plan(
            first, Read("daily@04:00/restart"), Utc, t0.AddMinutes(59));

        Assert.Same(first, later);
    }

    // The id is the schedule expression, so a window whose task set is edited is the SAME window —
    // its appointment did not move, and neither should the countdown anybody was told about.
    [Fact]
    public void Editing_only_the_tasks_keeps_the_standing_target()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        WindowPlan first = WindowPlanner.Plan(null, Read("daily@04:00/restart"), Utc, t0);

        Assert.Same(first, WindowPlanner.Plan(first, Read("daily@04:00/backup,restart"), Utc, t0));
    }

    [Fact]
    public void Changing_only_the_timezone_replaces_the_standing_target()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        ReadWindow read = Read("daily@04:00/restart");

        WindowPlan utcPlan = WindowPlanner.Plan(null, read, Utc, t0);
        WindowPlan madridPlan = WindowPlanner.Plan(utcPlan, read, madrid, t0);

        Assert.NotSame(utcPlan, madridPlan);
        Assert.NotEqual(utcPlan.NextUtc, madridPlan.NextUtc);
    }

    // An invalid window has no next fire, and the two facts together are what tell it apart from a
    // window that is simply not due.
    [Fact]
    public void A_window_this_host_will_not_fire_has_no_target()
    {
        WindowPlan plan = WindowPlanner.Plan(null, Read("weekly.someday@04:00/restart"), Utc, DateTime.UtcNow);

        Assert.Null(plan.NextUtc);
        Assert.False(WindowPlanner.IsDue(plan, DateTime.UtcNow.AddYears(1)));
    }

    // Two windows on one instance are independent appointments; neither gates the other.
    [Fact]
    public void Two_windows_carry_their_own_targets()
    {
        var t0 = new DateTime(2026, 8, 2, 3, 0, 0, DateTimeKind.Utc);

        WindowPlan backup = WindowPlanner.Plan(null, Read("daily@05:00/backup"), Utc, t0);
        WindowPlan restart = WindowPlanner.Plan(null, Read("daily@04:00/restart"), Utc, t0);

        Assert.Equal(new DateTime(2026, 8, 2, 5, 0, 0, DateTimeKind.Utc), backup.NextUtc);
        Assert.Equal(new DateTime(2026, 8, 2, 4, 0, 0, DateTimeKind.Utc), restart.NextUtc);
    }

    // ---- what this host will fire ------------------------------------------

    [Fact]
    public void A_window_that_reads_is_fired()
    {
        ReadWindow read = Read("weekly.sun@04:00/backup,restart");

        Assert.True(read.Valid);
        Assert.Null(read.Error);
    }

    // The parse error travels rather than being replaced by this daemon's own wording — kgsm-lib
    // names the offending text, and that is what an operator needs to find it.
    [Fact]
    public void A_window_that_does_not_parse_carries_the_parser_error()
    {
        ReadWindow read = Read("every thursday/restart");

        Assert.False(read.Valid);
        Assert.False(string.IsNullOrWhiteSpace(read.Error));
    }

    [Fact]
    public void A_window_more_frequent_than_the_host_floor_is_refused()
    {
        ReadWindow read = Read("15m/backup", 60);

        Assert.False(read.Valid);
        Assert.Contains("60 minute(s)", read.Error);
    }

    [Fact]
    public void A_window_at_the_host_floor_is_fired() =>
        Assert.True(Read("60m/backup", 60).Valid);

    /// <summary>
    /// What one particular instance cannot run is not a property of the window. A container's
    /// restart is declined by that task's own gate in the instant before dispatch, which is what
    /// leaves the archive written beside it still firing.
    /// </summary>
    [Theory]
    [InlineData("daily@04:00/restart")]
    [InlineData("daily@04:00/backup,restart")]
    [InlineData("weekly.sun@04:00/backup,update,restart")]
    public void A_window_carrying_something_disruptive_reads_fine(string expression) =>
        Assert.True(Read(expression).Valid);

    // A task the catalog does not hold is named when the window is read, so an operator hears about
    // it the moment they write it rather than a week later.
    [Fact]
    public void A_window_naming_a_task_this_host_does_not_run_is_refused()
    {
        var partial = new MaintenanceTaskCatalog([new BackupTask(NullLogger<BackupTask>.Instance)]);

        ReadWindow read = WindowPlanner.Read(
            MaintenanceWindowParser.ParseWindow("weekly.sun@04:00/backup,restart"), partial, 10, Utc, T0);

        Assert.False(read.Valid);
        Assert.Contains("restart", read.Error);
    }

    // ---- how far apart the fires are ---------------------------------------

    [Theory]
    [InlineData("daily@04:00/backup", 1440)]
    [InlineData("weekly.sun@04:00/backup", 10080)]
    [InlineData("10m/backup", 10)]
    [InlineData("6h/backup", 360)]
    [InlineData("30d/backup", 43200)]
    public void The_period_is_the_span_between_one_fire_and_the_next(string expression, int minutes) =>
        Assert.Equal(minutes,
            WindowPlanner.Period(MaintenanceWindowParser.ParseWindow(expression), Utc, T0).TotalMinutes);

    // An appointment's period is measured, not assumed. A month is 28 to 31 days, and guessing high
    // would let a grace window swallow an occurrence that was already due.
    [Theory]
    [InlineData(2026, 8, 30)]   // the gap lands on September, which has 30
    [InlineData(2026, 1, 28)]   // and here on February 2026, which has 28
    public void A_monthly_period_is_the_real_gap_to_the_next_fire(int year, int month, int days)
    {
        var from = new DateTime(year, month, 2, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(days,
            WindowPlanner.Period(MaintenanceWindowParser.ParseWindow("monthly.1@04:00/backup"), Utc, from).TotalDays);
    }

    // A daily appointment across a spring-forward transition is 23 hours, not 24.
    [Fact]
    public void A_daily_period_follows_the_clock_through_a_transition()
    {
        // The clocks go forward on the 29th, so the 28th's fire and the 29th's are 23 hours apart.
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
        var from = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(23,
            WindowPlanner.Period(MaintenanceWindowParser.ParseWindow("daily@04:00/backup"), madrid, from).TotalHours);
    }

    // ---- how late a fire may be --------------------------------------------

    // A window that comes round twice as often as the configured grace would otherwise have one
    // occurrence still owed when the next is already due, which is the catch-up burst the grace
    // exists to prevent arriving one fire at a time.
    [Fact]
    public void Grace_never_exceeds_half_a_windows_own_period() =>
        Assert.Equal(5, WindowPlanner.Grace(60, TimeSpan.FromMinutes(10), pollIntervalSeconds: 60));

    [Fact]
    public void A_window_far_enough_apart_gets_the_configured_grace() =>
        Assert.Equal(10, WindowPlanner.Grace(10, TimeSpan.FromDays(1), pollIntervalSeconds: 60));

    // A target is reached between ticks and acted on at the next one, so a fire is at best one poll
    // late. A grace under that would drop every window on the host, including one that had just come
    // due.
    [Fact]
    public void Grace_is_never_shorter_than_one_poll() =>
        Assert.Equal(2, WindowPlanner.Grace(0, TimeSpan.FromDays(1), pollIntervalSeconds: 120));

    // ---- what the status socket calls each kind ----------------------------

    [Theory]
    [InlineData("daily@04:00/backup", "appointment")]
    [InlineData("weekly.sun@04:00/backup", "appointment")]
    [InlineData("6h/backup", "interval")]
    [InlineData("monthly.1@04:00/backup", "appointment")]
    public void The_kind_is_named_by_the_schedule(string expression, string kind) =>
        Assert.Equal(kind, WindowPlanner.KindToken(MaintenanceWindowParser.ParseWindow(expression)));
}
