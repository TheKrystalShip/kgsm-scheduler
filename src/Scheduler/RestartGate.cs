using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>What the pre-dispatch state re-assert decided about a restart that has come due.</summary>
internal enum RestartGateOutcome
{
    /// <summary>The watchdog measures the instance as running, so the restart is dispatched.</summary>
    Dispatch,

    /// <summary>
    /// The restart does not apply to the instance as it stands, and nothing was owed: it is stopped,
    /// or the watchdog has given up on it.
    /// </summary>
    NotApplicable,

    /// <summary>The restart was owed and could not be dispatched.</summary>
    Blocked,
}

/// <summary>One gate verdict and the reason behind it, in the words the status socket reports.</summary>
internal readonly record struct RestartGateDecision(RestartGateOutcome Outcome, string Message)
{
    /// <summary>
    /// How the skip is recorded on the status socket. <c>false</c> is a failure — the schedule was
    /// owed a restart and did not get one, which a surface should raise. A restart that does not
    /// apply is recorded as neither: the clock came round for a server an operator had stopped, and
    /// declining is the correct outcome, so reporting it as failed would put a red row on the fleet
    /// page every night for a server nobody wants running. The time and the message still land, so
    /// the skip is never silent.
    /// <para>
    /// <c>true</c> belongs to a restart the watchdog actually performed and is never written here.
    /// </para>
    /// </summary>
    public bool? LastRunOk => Outcome == RestartGateOutcome.Blocked ? false : null;
}

/// <summary>
/// Decides, immediately before dispatch, whether a due restart still applies to the instance in
/// front of it.
/// </summary>
/// <remarks>
/// <para>
/// The clock says when to fire; only the watchdog knows what the instance is doing at that instant,
/// and the two answers are minutes apart. Two facts make the re-assert load-bearing. An operator
/// <c>start</c> is documented as clearing the give-up latch and the failure streak, so dispatching
/// into a crash-looping instance the supervisor has finished with wipes its failure history on a
/// timer. And a stop of an instance the daemon does not track succeeds as a no-op, so a restart of a
/// deliberately stopped server runs straight into its start half and spawns it fresh.
/// </para>
/// <para>
/// Every unknown fails closed. An unreachable watchdog is not a measurement of anything and never
/// reads as "not running"; the gate abandons and says the state could not be read, which is a
/// different sentence from the one it writes for a server the watchdog answers about and does not
/// track.
/// </para>
/// </remarks>
internal static class RestartGate
{
    public static async Task<RestartGateDecision> EvaluateAsync(
        IWatchdogClient watchdog, string name, InstanceRuntime? runtime, CancellationToken ct)
    {
        // Docker supervises container instances, and this daemon dispatches every restart through the
        // watchdog. Asked about one the watchdog answers 404, which is the same answer it gives for a
        // stopped native instance — so the runtime is read here rather than letting a schedule that
        // can never be honoured report itself as a server that happened to be down.
        if (runtime == InstanceRuntime.Container)
            return new(RestartGateOutcome.Blocked,
                "scheduled restart not dispatched: this is a container instance, and the watchdog this "
                + "daemon dispatches through supervises only native ones");

        WatchdogInstanceState? state;

        try
        {
            state = await watchdog.GetStatusAsync(name, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The tick's own probe marks the watchdog component degraded, so the daemon-wide fact is
            // already reported; this records which instance went unserved because of it.
            return new(RestartGateOutcome.Blocked,
                "scheduled restart not dispatched: the watchdog did not answer, so this instance's "
                + $"state is unknown ({ex.Message})");
        }

        return Decide(state);
    }

    /// <summary>Reads one watchdog state into a verdict.</summary>
    public static RestartGateDecision Decide(WatchdogInstanceState? state)
    {
        // Not tracked and not live: the daemon answers 404 for exactly that pair, so this is a
        // measured "nothing is running", not an absence of information.
        if (state is null)
            return new(RestartGateOutcome.NotApplicable,
                "scheduled restart not dispatched: the watchdog is not supervising this instance, "
                + "so it is not running");

        if (Phase(state, "failed"))
            return new(RestartGateOutcome.NotApplicable, Because(
                "scheduled restart not dispatched: the watchdog gave up on this instance after "
                + $"{state.Restarts} failed restart(s), and a restart here would clear that history",
                state.Reason));

        // Anything else the supervisor is mid-way through — a pending crash-restart above all — is the
        // watchdog still handling the instance, and a second supervisor acting on it is the boundary
        // this daemon is bound by.
        if (!Phase(state, "running"))
            return new(RestartGateOutcome.NotApplicable, Because(
                $"scheduled restart not dispatched: the watchdog reports phase '{state.Phase}'",
                state.Reason));

        // Phase is what the supervisor intends; Populated is what the kernel reports about the cgroup.
        // Only the second is evidence that there is a process to restart.
        if (!state.Populated)
            return new(RestartGateOutcome.NotApplicable, Because(
                "scheduled restart not dispatched: the watchdog holds this instance as running while "
                + "its cgroup is empty",
                state.Reason));

        return new(RestartGateOutcome.Dispatch, string.Empty);
    }

    private static bool Phase(WatchdogInstanceState state, string phase) =>
        string.Equals(state.Phase, phase, StringComparison.OrdinalIgnoreCase);

    private static string Because(string message, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? message : $"{message} — {reason.Trim()}";
}
