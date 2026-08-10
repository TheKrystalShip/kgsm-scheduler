using Microsoft.Extensions.Configuration;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// Configuration binding and normalization. These build a configuration in memory rather than
/// mutating the process environment, so they say nothing about ambient state and run in parallel.
/// </summary>
public class SchedulerOptionsTests
{
    /// <summary>Binds a settings section the way the daemon does, from the given key/value pairs.</summary>
    private static SchedulerOptions Bind(params (string Key, string? Value)[] values)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v =>
                new KeyValuePair<string, string?>($"{SchedulerSettings.Section}:{v.Key}", v.Value)))
            .Build();

        return SchedulerOptions.FromSettings(
            config.GetSection(SchedulerSettings.Section).Get<SchedulerSettings>() ?? new SchedulerSettings());
    }

    [Fact]
    public void Defaults_apply_when_nothing_is_configured()
    {
        var o = Bind();

        Assert.Equal("/usr/bin/kgsm", o.KgsmPath);
        Assert.Equal("/run/kgsm-watchdog/control.sock", o.WatchdogSocketPath);
        Assert.Equal("/run/kgsm-scheduler/status.sock", o.StatusSocketPath);
        Assert.Equal(60, o.PollIntervalSeconds);
        Assert.Equal(10, o.GraceWindowMinutes);
        Assert.True(o.UpdateCheckEnabled);
        Assert.Equal(60, o.UpdateCheckIntervalMinutes);
        Assert.Equal(5, o.UpdateCheckStaggerSeconds);
    }

    // Off has to be reachable and unambiguous: it means nothing on this host asks upstream, so no
    // update is ever announced. A default that could not be turned off would make the daemon
    // generate traffic an operator cannot stop.
    [Fact]
    public void Update_checks_can_be_turned_off()
    {
        Assert.False(Bind((nameof(SchedulerSettings.UpdateCheckEnabled), "false")).UpdateCheckEnabled);
    }

    // Same floor reasoning as the poll interval: the sweep drives a timer with this period, and the
    // roster is walked serially, so a value below the floor is raised rather than taken literally.
    [Theory]
    [InlineData("0", SchedulerOptions.MinUpdateCheckIntervalMinutes)]
    [InlineData("-1", SchedulerOptions.MinUpdateCheckIntervalMinutes)]
    [InlineData("1", SchedulerOptions.MinUpdateCheckIntervalMinutes)]
    [InlineData("360", 360)]
    public void Update_check_interval_is_raised_to_its_floor(string written, int expected)
    {
        Assert.Equal(expected,
            Bind((nameof(SchedulerSettings.UpdateCheckIntervalMinutes), written)).UpdateCheckIntervalMinutes);
    }

    // Zero is a legitimate stagger — check the roster back to back — so it is clamped at zero rather
    // than raised to a floor.
    [Fact]
    public void A_negative_stagger_becomes_zero()
    {
        Assert.Equal(0,
            Bind((nameof(SchedulerSettings.UpdateCheckStaggerSeconds), "-9")).UpdateCheckStaggerSeconds);
    }

    [Fact]
    public void Configured_values_are_bound()
    {
        var o = Bind(
            (nameof(SchedulerSettings.KgsmPath), "/opt/kgsm/kgsm.sh"),
            (nameof(SchedulerSettings.PollIntervalSeconds), "23"),
            (nameof(SchedulerSettings.GraceWindowMinutes), "0"));

        Assert.Equal("/opt/kgsm/kgsm.sh", o.KgsmPath);
        Assert.Equal(23, o.PollIntervalSeconds);
        Assert.Equal(0, o.GraceWindowMinutes);
    }

    // A poll interval below the floor is raised to it, not reverted to the default: the engine drives
    // a timer with this period, and a zero or negative one throws.
    [Theory]
    [InlineData("1", SchedulerOptions.MinPollIntervalSeconds)]
    [InlineData("0", SchedulerOptions.MinPollIntervalSeconds)]
    [InlineData("-30", SchedulerOptions.MinPollIntervalSeconds)]
    [InlineData("5", SchedulerOptions.MinPollIntervalSeconds)]
    [InlineData("90", 90)]
    public void Poll_interval_is_raised_to_its_floor(string written, int expected)
    {
        Assert.Equal(expected, Bind((nameof(SchedulerSettings.PollIntervalSeconds), written)).PollIntervalSeconds);
    }

    [Fact]
    public void A_negative_grace_window_becomes_zero()
    {
        Assert.Equal(0, Bind((nameof(SchedulerSettings.GraceWindowMinutes), "-5")).GraceWindowMinutes);
    }

    // A knob written blank is "unset", not a startup crash and not zero. Both failure modes are real
    // binder behaviour on a non-nullable property: a blank value throws, and a null one binds to 0 —
    // so `Scheduler__PollIntervalSeconds=` left in an env file would have taken the daemon down, and a
    // JSON null would have silently discarded the 60s default. Both numbers are nullable so they land
    // on the coded default instead.
    [Fact]
    public void A_blank_value_means_unset_and_takes_the_coded_default()
    {
        var o = Bind(
            (nameof(SchedulerSettings.PollIntervalSeconds), ""),
            (nameof(SchedulerSettings.GraceWindowMinutes), ""),
            (nameof(SchedulerSettings.UpdateCheckEnabled), ""),
            (nameof(SchedulerSettings.UpdateCheckIntervalMinutes), ""),
            (nameof(SchedulerSettings.UpdateCheckStaggerSeconds), ""));

        Assert.Equal(60, o.PollIntervalSeconds);
        Assert.Equal(10, o.GraceWindowMinutes);
        // Blank must not read as "off" — a stray line in an env file would silently stop this host
        // ever looking for an update, and nothing would say so.
        Assert.True(o.UpdateCheckEnabled);
        Assert.Equal(60, o.UpdateCheckIntervalMinutes);
        Assert.Equal(5, o.UpdateCheckStaggerSeconds);
    }

    // The other half of the contract: a value that is present but is not a number is NOT quietly
    // ignored. Typed configuration is worth having only if it refuses what it cannot read.
    [Fact]
    public void A_value_that_is_not_a_number_is_refused()
    {
        Assert.ThrowsAny<Exception>(() => Bind((nameof(SchedulerSettings.PollIntervalSeconds), "hourly")));
    }

    // A blank path falls back to the coded default rather than becoming an empty path, which would
    // make the daemon bind "" and never serve its status snapshot.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_paths_fall_back_to_their_defaults(string? written)
    {
        var o = Bind(
            (nameof(SchedulerSettings.KgsmPath), written),
            (nameof(SchedulerSettings.WatchdogSocketPath), written),
            (nameof(SchedulerSettings.StatusSocketPath), written));

        Assert.Equal("/usr/bin/kgsm", o.KgsmPath);
        Assert.Equal("/run/kgsm-watchdog/control.sock", o.WatchdogSocketPath);
        Assert.Equal("/run/kgsm-scheduler/status.sock", o.StatusSocketPath);
    }

    [Fact]
    public void Written_paths_are_trimmed()
    {
        Assert.Equal("/opt/kgsm/kgsm.sh",
            Bind((nameof(SchedulerSettings.KgsmPath), "  /opt/kgsm/kgsm.sh  ")).KgsmPath);
    }
}
