using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>What an instruction acts on, and however much it needs.</summary>
/// <param name="Instance">The server whose window it acts on.</param>
/// <param name="Window">The window's id — its schedule expression, e.g. <c>weekly.sun@04:00</c>.</param>
/// <param name="Minutes">How far <see cref="WindowControl.Postpone"/> pushes the next fire back.</param>
internal sealed record ControlRequest(string? Instance, string? Window, int? Minutes);

/// <summary>What came of it. <see cref="NextFireUtc"/> is the window as it now stands, so a caller
/// never has to ask again to find out what it just did.</summary>
internal sealed record ControlResponse(bool Ok, string Message, DateTimeOffset? NextFireUtc = null);

/// <summary>
/// What the scheduler can be <em>told</em>, as opposed to what it can be asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>It stays consumer-agnostic.</b> The scheduler does not know what a Control Panel is; these are
/// verbs any local client can send, and the one that motivated them — deferring tonight's restart from
/// a phone — is one caller among possible others.
/// </para>
/// <para>
/// <b>Every verb moves a standing target; none edits a schedule.</b> The instance's configuration is
/// untouched, so the fire after the one acted on lands exactly where it always would have. That is what
/// makes these "not tonight" and "just this once" rather than reschedules, and it is why they need
/// nothing from kgsm. It also means none of them survives a restart of this daemon: the standing target
/// lives in memory, and a restart recomputes it from the instance's own config.
/// </para>
/// <para>
/// <b>A verb names its window.</b> One instance can hold several appointments, and moving the wrong one
/// is worse than refusing — so an instruction that does not name a window is refused with the ids that
/// were available to name.
/// </para>
/// </remarks>
internal sealed class WindowControl(ScheduleRegistry registry, ILogger<WindowControl> logger)
{
    /// <summary>The most a single instruction may defer a fire. A postponement is "not right now"; past
    /// this it is a schedule change, and a schedule change belongs in the instance's own config where it
    /// survives a restart of this daemon.</summary>
    internal const int MaxMinutes = 12 * 60;

    /// <summary>Pushes one window's next fire back, leaving the one after it where it was.</summary>
    public ControlResponse Postpone(ControlRequest request)
    {
        int minutes = request.Minutes ?? 60;
        if (minutes <= 0 || minutes > MaxMinutes)
            return new ControlResponse(false, $"minutes must be between 1 and {MaxMinutes}");

        return Move(request, "postponed",
            w => w.Plan.NextUtc is { } next ? next.AddMinutes(minutes) : null,
            $"postponed {minutes} minute(s)");
    }

    /// <summary>
    /// Drops this one occurrence: the target moves to the fire after it, and nothing runs in between.
    /// </summary>
    public ControlResponse Skip(ControlRequest request) =>
        Move(request, "skipped", w =>
            w.Plan.NextUtc is { } next ? ScheduleClock.NextFire(w.Window, w.Timezone, next) : null,
            "this occurrence is skipped");

    /// <summary>
    /// Brings the window forward to now, so the next poll opens it.
    /// </summary>
    /// <remarks>
    /// The target is moved rather than the run being started here, so the window goes through exactly
    /// the sequence a scheduled one does — the same busy-slot claim, the same gates, the same record.
    /// A second path into a run would be a second set of rules about when one is allowed to happen.
    /// </remarks>
    public ControlResponse RunNow(ControlRequest request) =>
        Move(request, "brought forward", _ => DateTime.UtcNow, "the window opens at the next poll");

    /// <summary>
    /// Names the window, moves its standing target under the registry's lock, and says what it did.
    /// </summary>
    /// <remarks>
    /// Read-modify-write under the lock, so a tick landing in the middle cannot overwrite the new
    /// target with the one it read a moment ago. The signature is untouched, so the next tick keeps
    /// this target rather than recomputing one — a move a re-plan discarded a minute later would be
    /// no move at all.
    /// </remarks>
    private ControlResponse Move(
        ControlRequest request,
        string verb,
        Func<WindowState, DateTime?> target,
        string success)
    {
        if (string.IsNullOrWhiteSpace(request.Instance))
            return new ControlResponse(false, "no instance named");

        ScheduleState? state = registry.Get(request.Instance);
        if (state is null || state.Windows.Count == 0)
            return new ControlResponse(false, $"{request.Instance} has no maintenance windows");

        if (string.IsNullOrWhiteSpace(request.Window))
            return new ControlResponse(false,
                $"no window named; {request.Instance} has {Available(state)}");

        string windowId = request.Window.Trim();
        if (!state.Windows.ContainsKey(windowId))
            return new ControlResponse(false,
                $"{request.Instance} has no window '{windowId}'; it has {Available(state)}");

        DateTime? moved = null;

        registry.UpdateWindow(request.Instance, windowId, w =>
        {
            if (w.Plan.NextUtc is null) return w;
            moved = target(w);
            return moved is null ? w : w with { Plan = w.Plan with { NextUtc = moved } };
        });

        if (moved is null)
            return new ControlResponse(false,
                $"{request.Instance}'s '{windowId}' has no next fire to move");

        logger.LogInformation("{Instance}: {Window} {Verb} → {Next:o}",
            request.Instance, windowId, verb, moved);

        return new ControlResponse(true, success, new DateTimeOffset(moved.Value, TimeSpan.Zero));
    }

    private static string Available(ScheduleState state) =>
        string.Join(", ", state.Windows.Keys.Select(k => $"'{k}'"));
}
