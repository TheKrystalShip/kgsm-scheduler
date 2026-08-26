namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// The words an outcome is reported in, on the status socket and in this daemon's own log.
/// </summary>
/// <remarks>
/// Four words rather than a boolean, because "did the maintenance work" has four genuinely
/// different answers and collapsing them loses the one a person acts on. <see cref="Skipped"/> is
/// not a lesser <see cref="Failed"/>: the clock came round for something that does not apply — a
/// server an operator had already stopped — and declining is the correct act, so it is recorded
/// with its reason rather than raised. <see cref="Aborted"/> says a task never got its turn
/// because an earlier one in the same window failed, which is what makes "which part failed"
/// answerable.
/// </remarks>
internal static class MaintenanceOutcomes
{
    /// <summary>It was owed, and it happened.</summary>
    public const string Ok = "ok";

    /// <summary>It was owed, and it did not happen.</summary>
    public const string Failed = "failed";

    /// <summary>It did not apply to the instance as it stood.</summary>
    public const string Skipped = "skipped";

    /// <summary>An earlier task in the same window failed, so this one never ran.</summary>
    public const string Aborted = "aborted";
}

/// <summary>One task's turn inside a window run.</summary>
/// <param name="Name">The grammar token — <c>backup</c>, <c>update</c> or <c>restart</c>.</param>
/// <param name="Outcome">One of <see cref="MaintenanceOutcomes"/>.</param>
/// <param name="Message">Why, in the words a person reads. Null when there is nothing to add.</param>
internal sealed record MaintenanceTaskRun(string Name, string Outcome, string? Message);

/// <summary>
/// One run of one window: when it started, when it finished, how it came out, and what each of
/// its tasks did.
/// </summary>
/// <remarks>
/// The record belongs to the window that ran, and to no other. An instance with a nightly backup
/// window and a Sunday restart window keeps two of these, so a backup that fails is never reported
/// against the restart.
/// </remarks>
internal sealed record MaintenanceRun(
    DateTimeOffset StartedUtc,
    DateTimeOffset? FinishedUtc,
    string Outcome,
    IReadOnlyList<MaintenanceTaskRun> Tasks);

/// <summary>One maintenance window as this daemon holds it.</summary>
/// <param name="Id">The schedule expression, which is the window's identity.</param>
/// <param name="Kind"><c>appointment</c> or <c>interval</c>.</param>
/// <param name="Tasks">The tasks it runs, in canonical order.</param>
/// <param name="Valid">Whether this daemon will fire it.</param>
/// <param name="Error">Why it will not, when <paramref name="Valid"/> is false.</param>
/// <param name="NextFireUtc">
/// When it fires next. Null on an invalid window — the two facts together are what tell a window
/// that will never fire apart from one that is simply not due.
/// </param>
/// <param name="LastRun">The last run of this window, or null if it has not run since this daemon started.</param>
internal sealed record SchedulerWindowStatus(
    string Id,
    string Kind,
    IReadOnlyList<string> Tasks,
    bool Valid,
    string? Error,
    DateTimeOffset? NextFireUtc,
    MaintenanceRun? LastRun);

/// <summary>One server's maintenance, as this daemon holds it.</summary>
/// <remarks>
/// ⚠ The three update-check fields are <b>the sweep's own attempt</b>, not when the upstream was
/// last fetched. A server skipped as recently-checked is null here while the engine holds a real
/// <c>checked_at</c> for it, and a failed attempt has a time here with no new <c>checked_at</c>
/// there. They answer "is the sweep working, and what failed".
/// </remarks>
internal sealed record SchedulerInstanceStatus(
    string Name,
    string? Timezone,
    IReadOnlyList<SchedulerWindowStatus> Windows,
    DateTimeOffset? LastUpdateCheckUtc,
    bool? LastUpdateCheckOk,
    string? LastUpdateCheckMessage);

/// <summary>What the status socket serves, one NDJSON line per connection.</summary>
internal sealed record SchedulerStatusResponse(IReadOnlyList<SchedulerInstanceStatus> Instances);
