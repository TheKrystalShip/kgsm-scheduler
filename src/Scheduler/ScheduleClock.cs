namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Wall-clock arithmetic for the restart and backup schedules. Pure and side-effect free, so the
/// engine's timing can be tested without a host, a watchdog or a clock that actually advances.
/// </summary>
/// <remarks>
/// Every method answers "when does this schedule next fire, strictly after the given instant" —
/// it never answers "is it due now". A schedule is due when a target computed on an earlier tick
/// has been reached, which is why <see cref="SchedulerEngine"/> stores the target rather than
/// recomputing it each tick and comparing to the present.
/// </remarks>
internal static class ScheduleClock
{
    /// <summary>Whether a cadence value asks for anything to happen at all.</summary>
    public static bool IsActive(string? cadence) =>
        !string.IsNullOrWhiteSpace(cadence)
        && !string.Equals(cadence, "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The next fire time in UTC, strictly after <paramref name="after"/>. Null for an inactive
    /// cadence, an unknown cadence, or an unparseable time — never a guessed value.
    /// </summary>
    public static DateTime? ComputeNextFire(
        string? cadence, string? timeOfDay, string? day, TimeZoneInfo tz, DateTime after)
    {
        if (!IsActive(cadence)) return null;
        if (!TryParseTime(timeOfDay ?? "04:00", out int hour, out int minute)) return null;

        return cadence!.ToLowerInvariant() switch
        {
            "daily" => NextDailyFire(hour, minute, tz, after),
            "weekly" => NextWeeklyFire(hour, minute, day, tz, after),
            "6h" => NextIntervalFire(6, after),
            _ => null,
        };
    }

    public static DateTime NextDailyFire(int hour, int minute, TimeZoneInfo tz, DateTime after)
    {
        var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(after, tz);
        var todayFire = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day,
            hour, minute, 0, DateTimeKind.Unspecified);
        var fireLocal = todayFire > afterLocal ? todayFire : todayFire.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(fireLocal, tz);
    }

    public static DateTime NextWeeklyFire(int hour, int minute, string? day, TimeZoneInfo tz, DateTime after)
    {
        var target = ParseDayOfWeek(day ?? "sun");
        var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(after, tz);
        var todayFire = new DateTime(afterLocal.Year, afterLocal.Month, afterLocal.Day,
            hour, minute, 0, DateTimeKind.Unspecified);

        int daysUntil = ((int)target - (int)afterLocal.DayOfWeek + 7) % 7;
        if (daysUntil == 0 && todayFire <= afterLocal) daysUntil = 7;
        var fireLocal = todayFire.AddDays(daysUntil);
        return TimeZoneInfo.ConvertTimeToUtc(fireLocal, tz);
    }

    /// <summary>
    /// The next whole N-hour boundary after <paramref name="after"/>, measured from the Unix epoch
    /// in UTC. An interval cadence deliberately ignores the configured time of day and timezone:
    /// "every 6 hours" is an interval, not an appointment.
    /// </summary>
    public static DateTime NextIntervalFire(int intervalHours, DateTime after)
    {
        var total = (long)(after - DateTime.UnixEpoch).TotalHours;
        var nextBoundary = (total / intervalHours + 1) * intervalHours;
        return DateTime.UnixEpoch + TimeSpan.FromHours(nextBoundary);
    }

    public static bool TryParseTime(string hhmm, out int hour, out int minute)
    {
        hour = minute = 0;
        var parts = hhmm.Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute)
            && hour is >= 0 and <= 23 && minute is >= 0 and <= 59;
    }

    public static DayOfWeek ParseDayOfWeek(string s) => s.ToLowerInvariant() switch
    {
        "mon" => DayOfWeek.Monday,
        "tue" => DayOfWeek.Tuesday,
        "wed" => DayOfWeek.Wednesday,
        "thu" => DayOfWeek.Thursday,
        "fri" => DayOfWeek.Friday,
        "sat" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    public static TimeZoneInfo ResolveTimezone(string? iana)
    {
        if (string.IsNullOrWhiteSpace(iana)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(iana); }
        catch { return TimeZoneInfo.Local; }
    }
}
