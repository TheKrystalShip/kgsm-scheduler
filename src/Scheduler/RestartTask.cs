using Microsoft.Extensions.Logging;
using TheKrystalShip.KGSM.Core.Interfaces;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Bounces the instance, through the watchdog.
/// </summary>
/// <remarks>
/// <see cref="IWatchdogClient.RestartAsync"/> is one atomic transition the watchdog already owns:
/// it drains and respawns without incrementing the crash-recovery streak, and the instance is
/// never left in a state where desired-state says stopped. A stop/start pair from here would say
/// something different and would strand the server if this daemon died between the two.
/// </remarks>
internal sealed class RestartTask(ILogger<RestartTask> logger) : IMaintenanceTask
{
    public string Name => MaintenanceTask.Restart.ToToken();

    /// <summary>Everyone connected is disconnected, so a window carrying this is announced.</summary>
    public bool IsDisruptive => true;

    public Task<TaskGate> GateAsync(Instance instance, IWatchdogClient watchdog, CancellationToken ct) =>
        RestartGate.EvaluateAsync(watchdog, instance.Name, instance.Runtime, ct);

    public async Task<TaskOutcome> RunAsync(MaintenanceContext ctx, CancellationToken ct)
    {
        logger.LogInformation("{Instance}: firing scheduled restart", ctx.Name);

        WatchdogActionResult result = await ctx.Watchdog
            .RestartAsync(ctx.Name, Provenance.Leaf, ct)
            .ConfigureAwait(false);

        logger.LogInformation("{Instance}: restart {Result} — {Msg}",
            ctx.Name, result.Ok ? "ok" : "failed", result.Message);

        return result.Ok
            ? TaskOutcome.Ok()
            : TaskOutcome.Failed(string.IsNullOrWhiteSpace(result.Message)
                ? "the watchdog refused the restart and gave no detail"
                : result.Message);
    }
}
