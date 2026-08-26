using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// What the daemon accepts being told, and what it refuses.
/// <para>
/// Every verb moves a standing target and leaves the instance's configuration alone, which is the
/// whole distinction between "not tonight" and "change the schedule". Everything here is about
/// keeping that true, about a verb reaching the window it named and no other, and about a malformed
/// line never reaching the engine.
/// </para>
/// </summary>
public sealed class ControlSocketTests
{
    private static readonly DateTime Fire = new(2026, 8, 13, 4, 0, 0, DateTimeKind.Utc);
    private const string Restart = "daily@04:00";
    private const string Backup = "daily@05:00";

    private static (ControlSocketServer Server, ScheduleRegistry Registry) New(bool scheduled = true)
    {
        var registry = new ScheduleRegistry();

        if (scheduled)
        {
            registry.Set("factorio-01", new ScheduleState
            {
                Windows = new Dictionary<string, WindowState>(StringComparer.Ordinal)
                {
                    [Restart] = Window($"{Restart}/restart", Fire),
                    [Backup] = Window($"{Backup}/backup", Fire.AddHours(1)),
                },
            });
        }

        var server = new ControlSocketServer(
            Options.Create(new SchedulerOptions()), registry, NullLogger<ControlSocketServer>.Instance);
        return (server, registry);
    }

    /// <summary>Seeds one window the way a tick leaves it: planned, with a standing target.</summary>
    private static WindowState Window(string expression, DateTime next)
    {
        ReadWindow read = Read(expression);
        WindowPlan plan = WindowPlanner.Plan(null, read, TimeZoneInfo.Utc, next) with { NextUtc = next };
        return new WindowState(read.Window, TimeZoneInfo.Utc, plan, null);
    }

    private static ReadWindow Read(string expression)
    {
        MaintenanceWindow window = MaintenanceWindowParser.ParseWindow(expression);
        return new ReadWindow(window, window.IsValid, window.Error,
            WindowPlanner.Period(window, TimeZoneInfo.Utc, Fire));
    }

    // ---- postpone ----------------------------------------------------------

    [Fact]
    public void Postponing_moves_the_standing_target_and_nothing_else()
    {
        (ControlSocketServer server, ScheduleRegistry registry) = New();

        ControlResponse response = server.Handle(
            $$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}","minutes":60}""");

        Assert.True(response.Ok);
        Assert.Equal(Fire.AddHours(1), response.NextFireUtc!.Value.UtcDateTime);

        ScheduleState state = registry.Get("factorio-01")!;
        Assert.Equal(Fire.AddHours(1), state.Windows[Restart].Plan.NextUtc);
        // The window is untouched, so the id, the tasks and the schedule all still read as they did.
        Assert.Equal($"{Restart}/restart", state.Windows[Restart].Window.ToExpression());
    }

    /// <summary>
    /// The instance's other window is an independent appointment. Moving the wrong one is the
    /// failure a per-window verb exists to make impossible.
    /// </summary>
    [Fact]
    public void A_verb_reaches_the_window_it_named_and_no_other()
    {
        (ControlSocketServer server, ScheduleRegistry registry) = New();

        server.Handle($$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}"}""");

        Assert.Equal(Fire.AddHours(1), registry.Get("factorio-01")!.Windows[Backup].Plan.NextUtc);
    }

    [Fact]
    public void The_fire_after_this_one_is_unaffected()
    {
        // Nothing about the instance's configuration changed, so the next tick's re-plan keeps the
        // moved target and the fire after it lands where it always would have.
        (ControlSocketServer server, ScheduleRegistry registry) = New();
        server.Handle($$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}"}""");

        // The signature is untouched, so the next tick keeps this target rather than recomputing one:
        // a postponement a re-plan discarded a minute later would be no postponement at all.
        WindowState window = registry.Get("factorio-01")!.Windows[Restart];

        Assert.Same(window.Plan,
            WindowPlanner.Plan(window.Plan, Read($"{Restart}/restart"), TimeZoneInfo.Utc, Fire.AddHours(2)));
    }

    [Fact]
    public void An_hour_is_the_default_because_that_is_what_the_button_says() =>
        Assert.Equal(Fire.AddHours(1),
            New().Server.Handle($$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}"}""")
                .NextFireUtc!.Value.UtcDateTime);

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(ControlSocketServer.MaxMinutes + 1)]
    public void A_postponement_has_a_ceiling(int minutes)
    {
        // Past the ceiling it is a schedule change, and a schedule change belongs in the instance's own
        // config where it survives a restart of this daemon.
        ControlResponse response = New().Server.Handle(
            $$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}","minutes":{{minutes}}}""");

        Assert.False(response.Ok);
        Assert.Contains("between 1 and", response.Message);
    }

    [Fact]
    public void Postponing_twice_defers_twice()
    {
        // Each instruction is "not for another hour", so two of them mean two hours. The alternative —
        // clamping to one postponement — would silently ignore the second tap.
        (ControlSocketServer server, ScheduleRegistry _) = New();
        server.Handle($$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}"}""");
        ControlResponse second = server.Handle(
            $$"""{"command":"postpone","instance":"factorio-01","window":"{{Restart}}"}""");

        Assert.Equal(Fire.AddHours(2), second.NextFireUtc!.Value.UtcDateTime);
    }

    // ---- skip --------------------------------------------------------------

    /// <summary>
    /// Skipping drops one occurrence: the target lands on the fire after it, computed from the same
    /// window and timezone the tick planned.
    /// </summary>
    [Fact]
    public void Skipping_moves_the_target_to_the_next_occurrence()
    {
        ControlResponse response = New().Server.Handle(
            $$"""{"command":"skip","instance":"factorio-01","window":"{{Restart}}"}""");

        Assert.True(response.Ok);
        Assert.Equal(Fire.AddDays(1), response.NextFireUtc!.Value.UtcDateTime);
    }

    // ---- run-now -----------------------------------------------------------

    /// <summary>
    /// Bringing a window forward moves its target rather than starting a run here, so it goes through
    /// exactly the sequence a scheduled one does — the same busy-slot claim, the same gates, the same
    /// record.
    /// </summary>
    [Fact]
    public void Running_now_brings_the_target_to_the_present()
    {
        (ControlSocketServer server, ScheduleRegistry registry) = New();
        var before = DateTime.UtcNow;

        ControlResponse response = server.Handle(
            $$"""{"command":"run-now","instance":"factorio-01","window":"{{Restart}}"}""");

        Assert.True(response.Ok);
        DateTime moved = registry.Get("factorio-01")!.Windows[Restart].Plan.NextUtc!.Value;
        Assert.InRange(moved, before, DateTime.UtcNow);
        Assert.True(WindowPlanner.IsDue(registry.Get("factorio-01")!.Windows[Restart].Plan, DateTime.UtcNow));
    }

    // ---- what it refuses ---------------------------------------------------

    [Fact]
    public void An_instance_with_no_windows_is_told_so()
    {
        ControlResponse response = New(scheduled: false).Server.Handle(
            """{"command":"postpone","instance":"factorio-01","window":"daily@04:00"}""");

        Assert.False(response.Ok);
        Assert.Contains("no maintenance windows", response.Message);
    }

    /// <summary>
    /// One instance can hold several appointments, so moving the wrong one is worse than refusing.
    /// The refusal names what was available to name.
    /// </summary>
    [Theory]
    [InlineData("postpone")]
    [InlineData("skip")]
    [InlineData("run-now")]
    public void A_verb_that_names_no_window_is_refused_with_the_ids_it_could_have_named(string command)
    {
        ControlResponse response = New().Server.Handle(
            $$"""{"command":"{{command}}","instance":"factorio-01"}""");

        Assert.False(response.Ok);
        Assert.Contains("no window named", response.Message);
        Assert.Contains(Restart, response.Message);
        Assert.Contains(Backup, response.Message);
    }

    [Fact]
    public void A_window_this_instance_does_not_have_is_refused()
    {
        ControlResponse response = New().Server.Handle(
            """{"command":"skip","instance":"factorio-01","window":"weekly.sun@04:00"}""");

        Assert.False(response.Ok);
        Assert.Contains("no window 'weekly.sun@04:00'", response.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    public void A_line_that_is_not_a_request_is_refused_rather_than_guessed_at(string? line)
    {
        ControlResponse response = New().Server.Handle(line);
        Assert.False(response.Ok);
        // And the registry is untouched — a malformed line must never reach a schedule.
        Assert.Equal(Fire, New().Registry.Get("factorio-01")!.Windows[Restart].Plan.NextUtc);
    }

    [Fact]
    public void An_unknown_verb_names_itself_in_the_refusal()
    {
        ControlResponse response = New().Server.Handle("""{"command":"cancel","instance":"factorio-01"}""");
        Assert.False(response.Ok);
        Assert.Contains("cancel", response.Message);
    }

    [Fact]
    public void A_request_naming_no_instance_does_nothing()
    {
        ControlResponse response = New().Server.Handle("""{"command":"postpone"}""");
        Assert.False(response.Ok);
        Assert.Contains("no instance", response.Message);
    }

    /// <summary>An invalid window has no fire to move, and nothing here invents one.</summary>
    [Fact]
    public void A_window_with_no_next_fire_has_nothing_to_move()
    {
        var registry = new ScheduleRegistry();
        registry.Set("factorio-01", new ScheduleState
        {
            Windows = new Dictionary<string, WindowState>(StringComparer.Ordinal)
            {
                ["every thursday"] = new(
                    MaintenanceWindowParser.ParseWindow("every thursday/restart"), TimeZoneInfo.Utc,
                    new WindowPlan("every thursday|off|UTC", null), null),
            },
        });

        var server = new ControlSocketServer(
            Options.Create(new SchedulerOptions()), registry, NullLogger<ControlSocketServer>.Instance);

        ControlResponse response = server.Handle(
            """{"command":"postpone","instance":"factorio-01","window":"every thursday"}""");

        Assert.False(response.Ok);
        Assert.Contains("no next fire", response.Message);
    }
}
