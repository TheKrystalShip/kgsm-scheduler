using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.Kgsm.Scheduler;

namespace Scheduler.Tests;

/// <summary>
/// What the daemon accepts being told, and what it refuses.
/// <para>
/// A postponement moves the standing target and leaves the instance's configuration alone, which is the
/// whole distinction between "not tonight" and "change the schedule". Everything here is about keeping
/// that true, and about a malformed line never reaching the engine.
/// </para>
/// </summary>
public sealed class ControlSocketTests
{
    private static readonly DateTime Fire = new(2026, 8, 13, 4, 0, 0, DateTimeKind.Utc);

    private static (ControlSocketServer Server, ScheduleRegistry Registry) New(bool scheduled = true)
    {
        var registry = new ScheduleRegistry();
        if (scheduled)
            registry.Set("factorio-01", new ScheduleState(Restart: new SchedulePlan("daily|04:00||UTC", Fire)));

        var server = new ControlSocketServer(
            Options.Create(new SchedulerOptions()), registry, NullLogger<ControlSocketServer>.Instance);
        return (server, registry);
    }

    [Fact]
    public void Postponing_moves_the_standing_target_and_nothing_else()
    {
        (ControlSocketServer server, ScheduleRegistry registry) = New();

        ControlResponse response = server.Handle(
            """{"command":"postpone","instance":"factorio-01","minutes":60}""");

        Assert.True(response.Ok);
        Assert.Equal(Fire.AddHours(1), response.NextFireUtc!.Value.UtcDateTime);

        ScheduleState? state = registry.Get("factorio-01");
        Assert.Equal(Fire.AddHours(1), state!.Restart!.NextUtc);
        // The signature is untouched, so the next tick keeps this target rather than recomputing one:
        // a postponement that a re-plan discarded a minute later would be no postponement at all.
        Assert.Equal("daily|04:00||UTC", state.Restart.Signature);
    }

    [Fact]
    public void The_fire_after_this_one_is_unaffected()
    {
        // Nothing about the instance's configuration changed, so Plan() still recomputes from the same
        // cadence once this fire is spent. That is what makes it a postponement and not an edit.
        (ControlSocketServer server, ScheduleRegistry registry) = New();
        server.Handle("""{"command":"postpone","instance":"factorio-01","minutes":60}""");

        var tz = TimeZoneInfo.Utc;
        SchedulePlan replanned = SchedulerEngine.Plan(
            registry.Get("factorio-01")!.Restart, "daily", "04:00", null, tz, Fire.AddHours(2));

        Assert.Equal(Fire.AddHours(1), replanned.NextUtc);
    }

    [Fact]
    public void An_hour_is_the_default_because_that_is_what_the_button_says() =>
        Assert.Equal(Fire.AddHours(1),
            New().Server.Handle("""{"command":"postpone","instance":"factorio-01"}""")
                .NextFireUtc!.Value.UtcDateTime);

    [Fact]
    public void An_instance_with_nothing_scheduled_is_told_so()
    {
        ControlResponse response = New(scheduled: false).Server.Handle(
            """{"command":"postpone","instance":"factorio-01","minutes":60}""");

        Assert.False(response.Ok);
        Assert.Contains("no scheduled restart", response.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(ControlSocketServer.MaxMinutes + 1)]
    public void A_postponement_has_a_ceiling(int minutes)
    {
        // Past the ceiling it is a schedule change, and a schedule change belongs in the instance's own
        // config where it survives a restart of this daemon.
        ControlResponse response = New().Server.Handle(
            $$"""{"command":"postpone","instance":"factorio-01","minutes":{{minutes}}}""");

        Assert.False(response.Ok);
        Assert.Contains("between 1 and", response.Message);
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
        // And the registry is untouched — a malformed line must never reach the schedule.
        Assert.Equal(Fire, New().Registry.Get("factorio-01")!.Restart!.NextUtc);
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

    [Fact]
    public void Postponing_twice_defers_twice()
    {
        // Each instruction is "not for another hour", so two of them mean two hours. The alternative —
        // clamping to one postponement — would silently ignore the second tap.
        (ControlSocketServer server, ScheduleRegistry _) = New();
        server.Handle("""{"command":"postpone","instance":"factorio-01","minutes":60}""");
        ControlResponse second = server.Handle("""{"command":"postpone","instance":"factorio-01","minutes":60}""");

        Assert.Equal(Fire.AddHours(2), second.NextFireUtc!.Value.UtcDateTime);
    }
}
