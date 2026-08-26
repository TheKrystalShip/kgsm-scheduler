using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// A standing appointment: the configuration it was derived from, and the instant it next fires.
/// </summary>
/// <remarks>
/// The target is held across ticks rather than recomputed from the present each time, because
/// "next fire after now" is always in the future — comparing it to now could never say "due".
/// The signature is what makes an edited window take effect: when it changes, the stored target is
/// discarded and a new one computed. A window's id <em>is</em> its schedule expression, so the id
/// carries almost the whole signature; the timezone the appointment is read in, and whether this
/// host will fire the window at all, are the rest of it.
/// </remarks>
internal sealed record WindowPlan(string Signature, DateTime? NextUtc);

/// <summary>
/// A window as this daemon will act on it: what kgsm-lib read, plus what this host permits.
/// </summary>
/// <param name="Window">The window as written.</param>
/// <param name="Valid">Whether this daemon will fire it.</param>
/// <param name="Error">Why it will not, when <paramref name="Valid"/> is false.</param>
/// <param name="Period">The span between one fire and the next.</param>
internal sealed record ReadWindow(MaintenanceWindow Window, bool Valid, string? Error, TimeSpan Period);

/// <summary>
/// The arithmetic around a window that is this daemon's rather than kgsm-lib's: whether this host
/// will fire it, how far apart its fires are, and how late one may be and still run.
/// </summary>
internal static class WindowPlanner
{
    /// <summary>
    /// Returns the plan to use this tick: the standing one while its configuration is unchanged,
    /// otherwise a fresh target computed from now.
    /// </summary>
    internal static WindowPlan Plan(WindowPlan? existing, ReadWindow read, TimeZoneInfo tz, DateTime now)
    {
        string signature = string.Join('|', read.Window.Id, read.Valid ? "on" : "off", tz.Id);
        if (existing is not null && existing.Signature == signature)
            return existing;

        return new WindowPlan(
            signature,
            read.Valid ? ScheduleClock.NextFire(read.Window, tz, now) : null);
    }

    /// <summary>Whether a standing target has been reached.</summary>
    internal static bool IsDue(WindowPlan plan, DateTime now) =>
        plan.NextUtc is { } target && now >= target;

    /// <summary>
    /// The span between one fire of a window and the next.
    /// </summary>
    /// <remarks>
    /// An interval carries its own span. An appointment is measured rather than assumed — the gap
    /// between its next two fires — because the assumption is wrong wherever it matters: a month is
    /// 28 to 31 days, and a day across a daylight-saving transition is 23 or 25 hours. The period
    /// bounds a grace window and drops announcement leads, and both are safe when it is measured and
    /// wrong when it is guessed high.
    /// </remarks>
    internal static TimeSpan Period(MaintenanceWindow window, TimeZoneInfo tz, DateTime now)
    {
        if (!window.IsValid) return TimeSpan.Zero;

        if (window.Kind == MaintenanceScheduleKind.Interval)
            return window.Interval ?? TimeSpan.Zero;

        IReadOnlyList<DateTime> fires = ScheduleClock.NextFires(window, tz, now, 2);
        return fires.Count == 2 ? fires[1] - fires[0] : TimeSpan.Zero;
    }

    /// <summary>
    /// How late a fire of this window may be and still run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The configured grace is one host-wide number and a window's period is as short as ten
    /// minutes, so the flat value on its own would let an occurrence still be inside its grace when
    /// the next one is already due — the catch-up burst the grace exists to prevent, arriving one
    /// fire at a time and never emptying. Half the period is the widest grace under which at most
    /// one occurrence is ever in flight, so the configured value is capped there rather than
    /// replaced by it: a host asking for five minutes gets five on a nightly window and five on a
    /// ten-minute one.
    /// </para>
    /// <para>
    /// The floor is one poll. A target is reached between ticks and acted on at the next one, so a
    /// fire is at best one poll interval late; a grace under that would drop every window on the
    /// host as missed, including the one that had just come due.
    /// </para>
    /// </remarks>
    internal static double Grace(int configuredMinutes, TimeSpan period, int pollIntervalSeconds)
    {
        double floor = pollIntervalSeconds / 60d;
        double cap = period > TimeSpan.Zero ? period.TotalMinutes / 2 : double.MaxValue;

        return Math.Clamp(configuredMinutes, floor, Math.Max(floor, cap));
    }

    /// <summary>
    /// Reads one window as this host will act on it.
    /// </summary>
    /// <remarks>
    /// Everything that stops a window firing is decided here, once, when the window is read — never
    /// discovered as a failed run every week. An operator who writes a window this host cannot
    /// honour hears about it on the next poll, on the status socket and in the leaf's own health.
    /// </remarks>
    /// <remarks>
    /// What is refused here is what leaves the window with nothing to fire at all. A task that one
    /// particular instance cannot run is a different question, answered by that task's own gate in
    /// the instant before dispatch — so a container's nightly archive still happens even though the
    /// restart beside it never can.
    /// </remarks>
    /// <param name="window">The window kgsm-lib read.</param>
    /// <param name="catalog">The tasks this daemon can run.</param>
    /// <param name="minimumPeriodMinutes">The shortest period this host permits a window to have.</param>
    /// <param name="tz">The instance's timezone, for measuring an appointment's period.</param>
    /// <param name="now">The instant to measure the period from.</param>
    internal static ReadWindow Read(
        MaintenanceWindow window,
        MaintenanceTaskCatalog catalog,
        int minimumPeriodMinutes,
        TimeZoneInfo tz,
        DateTime now)
    {
        TimeSpan period = Period(window, tz, now);

        if (!window.IsValid)
            return new ReadWindow(window, false, window.Error, period);

        if (period.TotalMinutes < minimumPeriodMinutes)
            return new ReadWindow(window, false,
                $"this host runs maintenance no more often than every {minimumPeriodMinutes} minute(s)",
                period);

        foreach (MaintenanceTask task in window.Tasks)
        {
            // A task no instance on this host can run is a fact about the host, so it is stated once
            // where the window is read rather than as a failed run every week.
            if (catalog.Find(task) is null)
                return new ReadWindow(window, false,
                    $"the scheduler on this host does not run the '{task.ToToken()}' task", period);
        }

        return new ReadWindow(window, true, null, period);
    }

    /// <summary>The word the status socket names a window's schedule kind with.</summary>
    internal static string KindToken(MaintenanceWindow window) =>
        window.Kind == MaintenanceScheduleKind.Interval ? "interval" : "appointment";
}
