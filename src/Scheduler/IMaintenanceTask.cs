using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>What the pre-dispatch state re-assert decided about a task that has come due.</summary>
internal enum TaskGateOutcome
{
    /// <summary>The instance's state warrants the task, so it is dispatched.</summary>
    Dispatch,

    /// <summary>
    /// The task does not apply to the instance as it stands, and nothing was owed: it is stopped,
    /// or the watchdog has given up on it.
    /// </summary>
    Skip,

    /// <summary>The task was owed and could not be dispatched.</summary>
    Fail,
}

/// <summary>One gate verdict and the reason behind it, in the words the status socket reports.</summary>
/// <remarks>
/// The tri-state is the whole point. A task owed and not delivered is a failure a surface should
/// raise; a task that does not apply is not — the clock came round for a server that is
/// deliberately down, and declining is the right outcome. Both are recorded with their reason, so
/// neither is ever silent, and only the first puts a red row on a fleet page.
/// </remarks>
internal readonly record struct TaskGate(TaskGateOutcome Outcome, string Message)
{
    /// <summary>Run it.</summary>
    public static TaskGate Dispatch { get; } = new(TaskGateOutcome.Dispatch, string.Empty);

    /// <summary>It does not apply. Recorded, not raised.</summary>
    public static TaskGate Skip(string reason) => new(TaskGateOutcome.Skip, reason);

    /// <summary>It was owed and could not happen.</summary>
    public static TaskGate Fail(string reason) => new(TaskGateOutcome.Fail, reason);
}

/// <summary>How a task's own work came out.</summary>
internal readonly record struct TaskOutcome(string Outcome, string? Message)
{
    /// <summary>It happened. A message may still carry detail worth reading.</summary>
    public static TaskOutcome Ok(string? message = null) => new(MaintenanceOutcomes.Ok, message);

    /// <summary>It was owed and did not happen. Aborts the rest of the window.</summary>
    public static TaskOutcome Failed(string message) => new(MaintenanceOutcomes.Failed, message);

    /// <summary>It did not apply. The window carries on.</summary>
    public static TaskOutcome Skipped(string reason) => new(MaintenanceOutcomes.Skipped, reason);
}

/// <summary>
/// What the tasks of one window run have already done to the instance between them.
/// </summary>
/// <remarks>
/// Tasks are stateless singletons serving every instance on the host, so the one thing a task needs
/// to know about the task before it lives here, on the run.
/// </remarks>
internal sealed class WindowProgress
{
    /// <summary>
    /// Whether the instance has already been drained and brought back up inside this window.
    /// </summary>
    /// <remarks>
    /// Set from the measured release, never from having asked for one: a park whose release the
    /// watchdog refused left the instance down, and reading that as a delivered bounce would report
    /// a restart nobody got.
    /// </remarks>
    public bool InstanceCycled { get; private set; }

    /// <summary>Records that the instance went down and came back within this run.</summary>
    public void MarkCycled() => InstanceCycled = true;
}

/// <summary>Everything a task needs to do its work, and nothing about the tasks beside it.</summary>
/// <param name="Name">The instance's id, as kgsm and the watchdog know it.</param>
/// <param name="Instance">The instance's configuration, read on the tick that scheduled this run.</param>
/// <param name="Window">The window this run belongs to.</param>
/// <param name="Instances">The engine.</param>
/// <param name="Watchdog">The daemon every disruptive act is issued through.</param>
/// <param name="Progress">What the tasks before this one already did to the instance.</param>
internal sealed record MaintenanceContext(
    string Name,
    Instance Instance,
    MaintenanceWindow Window,
    IInstanceService Instances,
    IWatchdogClient Watchdog,
    WindowProgress Progress);

/// <summary>
/// One unit of work a maintenance window performs against an instance.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the extension point.</b> A task is one class here, one grammar token in kgsm-lib's
/// parser, one entry in the API's token set and one toggle in the Control Panel. It adds no
/// cadence, no plan, and no field on the status socket — a window already reports one row per task
/// it ran.
/// </para>
/// <para>
/// The gate and the work are separate calls because they answer at different instants. The clock
/// says when; only the watchdog knows what the instance is doing by the time the window opens, and
/// the two answers are minutes apart.
/// </para>
/// </remarks>
internal interface IMaintenanceTask
{
    /// <summary>The grammar token this task is written as — <c>backup</c>, <c>update</c>, <c>restart</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Whether running this interrupts the people on the server.
    /// </summary>
    /// <remarks>
    /// Two things read this. A window carrying one is announced beforehand, and the announcement's
    /// <c>{reason}</c> is resolved from which ones it carries. And every disruptive task is
    /// performed by the watchdog, which supervises native instances alone — so a container window
    /// carrying one is refused when it is read, rather than discovered as a failed run every week.
    /// </remarks>
    bool IsDisruptive { get; }

    /// <summary>
    /// Decides, immediately before dispatch, whether this task still applies to the instance in
    /// front of it.
    /// </summary>
    /// <param name="ctx">The instance, the window, and what the tasks before this one have done.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<TaskGate> GateAsync(MaintenanceContext ctx, CancellationToken ct);

    /// <summary>Does the work.</summary>
    /// <param name="ctx">The instance, the window, and the services to act through.</param>
    /// <param name="ct">Cancels the request.</param>
    Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct);
}

/// <summary>Who this daemon acts as when it drives the engine or the watchdog.</summary>
/// <remarks>
/// Every provenance-aware verb takes these. Omitting them attributes an unattended maintenance
/// run to whoever owns this process, and the audit trail then reads as a person having asked.
/// </remarks>
internal static class Provenance
{
    /// <summary>The actor. The <c>system:</c> form is what a consumer reads as an autonomous leaf.</summary>
    public const string Actor = "system:scheduler";

    /// <summary>The surface a human drove. A scheduled run has none.</summary>
    public const string Origin = "system";

    /// <summary>The requesting leaf, as the watchdog's own verbs name it.</summary>
    public const string Leaf = "scheduler";
}

/// <summary>The tasks this daemon can run, looked up by the token a window writes them as.</summary>
/// <remarks>
/// A window may name a task this daemon does not run. That is reported when the window is read —
/// as an invalid window carrying the reason — rather than each time it comes due, so an operator
/// hears about it the moment they write it and not a week later.
/// </remarks>
internal sealed class MaintenanceTaskCatalog(IEnumerable<IMaintenanceTask> tasks)
{
    private readonly Dictionary<string, IMaintenanceTask> _tasks =
        tasks.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>The implementation of <paramref name="task"/>, or null if this daemon has none.</summary>
    public IMaintenanceTask? Find(MaintenanceTask task) => _tasks.GetValueOrDefault(task.ToToken());
}
