using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// The update task: what it refuses to stop a server for, and the park it brackets the engine call
/// with.
/// </summary>
public sealed class UpdateTaskTests
{
    private const string Name = "factorio-01";

    private readonly InstanceServiceStub _instances = new();

    private static MaintenanceContext Context(
        InstanceServiceStub instances,
        WatchdogClientStub watchdog,
        WindowProgress? progress = null,
        InstanceRuntime runtime = InstanceRuntime.Native) =>
        new(Name,
            new Instance { Name = Name, DisplayName = "Factorio", Runtime = runtime },
            MaintenanceWindowParser.ParseWindow("daily@04:00/update"),
            instances,
            watchdog,
            progress ?? new WindowProgress());

    private static UpdateTask NewTask(bool updateChecksEnabled = true) =>
        new(Options.Create(new SchedulerOptions { UpdateCheckEnabled = updateChecksEnabled }),
            NullLogger<UpdateTask>.Instance);

    // ---- the gate: the engine's recorded latest decides ---------------------

    /// <summary>
    /// The reason the gate exists. Asking upstream costs a real steamcmd login, and an update with
    /// nothing to do is a server stopped for nothing.
    /// </summary>
    [Fact]
    public async Task An_instance_on_the_latest_recorded_build_is_skipped()
    {
        _instances.Recorded(Name, current: "1.1.110", latest: "1.1.110", updatesAvailable: false);

        TaskGate gate = await NewTask().GateAsync(
            Context(_instances, WatchdogClientStub.Answering(WatchdogClientStub.Running(Name))), default);

        Assert.Equal(TaskGateOutcome.Skip, gate.Outcome);
        Assert.Contains("no newer build stands", gate.Message);
        Assert.Contains("1.1.110", gate.Message);
        // The reading is taken off disk: the fleet read is the one that touches no network.
        Assert.Equal(["statuses:fast=True"], _instances.Calls);
    }

    [Fact]
    public async Task A_stale_instance_is_dispatched()
    {
        _instances.Recorded(Name, current: "1.1.87", latest: "1.1.110", updatesAvailable: true);

        TaskGate gate = await NewTask().GateAsync(
            Context(_instances, WatchdogClientStub.Answering(WatchdogClientStub.Running(Name))), default);

        Assert.Equal(TaskGateOutcome.Dispatch, gate.Outcome);
    }

    /// <summary>
    /// An unrecorded upstream is not evidence that an update is owed, so nothing is stopped for it —
    /// and with the sweep off, the skip says why nothing will ever record one.
    /// </summary>
    [Theory]
    [InlineData(true, "yet")]
    [InlineData(false, "update checks are disabled on this host")]
    public async Task An_instance_nothing_has_checked_is_skipped(bool sweepEnabled, string expected)
    {
        _instances.Recorded(Name, current: "1.1.87", latest: null, updatesAvailable: null);

        TaskGate gate = await NewTask(sweepEnabled).GateAsync(
            Context(_instances, WatchdogClientStub.Answering(WatchdogClientStub.Running(Name))), default);

        Assert.Equal(TaskGateOutcome.Skip, gate.Outcome);
        Assert.Contains(expected, gate.Message);
    }

    [Fact]
    public async Task An_instance_the_engine_reports_nothing_for_is_skipped()
    {
        TaskGate gate = await NewTask().GateAsync(
            Context(_instances, WatchdogClientStub.Answering(WatchdogClientStub.Running(Name))), default);

        Assert.Equal(TaskGateOutcome.Skip, gate.Outcome);
        Assert.Contains("no version for this instance", gate.Message);
    }

    /// <summary>
    /// The watchdog supervises native instances alone, so the park this runs behind is out of reach —
    /// and declining leaves the archive written beside it in the same window still firing.
    /// </summary>
    [Fact]
    public async Task A_container_is_declined_without_reading_a_version()
    {
        _instances.Recorded(Name, current: "1.1.87", latest: "1.1.110", updatesAvailable: true);

        TaskGate gate = await NewTask().GateAsync(
            Context(_instances, WatchdogClientStub.Answering(WatchdogClientStub.Running(Name)),
                runtime: InstanceRuntime.Container),
            default);

        Assert.Equal(TaskGateOutcome.Skip, gate.Outcome);
        Assert.Contains("container instance", gate.Message);
        Assert.Empty(_instances.Calls);
    }

    // ---- the run: the park brackets the engine call -------------------------

    [Fact]
    public async Task The_update_runs_between_a_park_and_a_release()
    {
        var watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Running(Name));
        watchdog.Calls = _instances.Calls;

        var progress = new WindowProgress();
        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog, progress), default);

        Assert.Equal(MaintenanceOutcomes.Ok, outcome.Outcome);
        Assert.Equal(
            [$"park:{Name}:scheduler", $"update:{Name}:system:scheduler:system", $"release:{Name}:scheduler"],
            _instances.Calls);
        Assert.True(progress.InstanceCycled);
    }

    /// <summary>
    /// The release is what makes "a window never leaves a server down" true, so it does not depend on
    /// the update having worked.
    /// </summary>
    [Fact]
    public async Task A_failed_update_is_released_all_the_same()
    {
        _instances.UpdateExitCode = 1;
        _instances.UpdateStderr = "steamcmd: could not reach the content servers";

        var watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Running(Name));
        watchdog.Calls = _instances.Calls;

        var progress = new WindowProgress();
        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog, progress), default);

        Assert.Equal(MaintenanceOutcomes.Failed, outcome.Outcome);
        Assert.Contains("content servers", outcome.Message);
        Assert.Equal($"release:{Name}:scheduler", _instances.Calls[^1]);
        // The instance is back up, which is the bounce a restart in the same window asks for.
        Assert.True(progress.InstanceCycled);
    }

    /// <summary>
    /// The server is down and no later task will bring it back, so the window says so — whatever the
    /// engine's half came to.
    /// </summary>
    [Fact]
    public async Task A_release_the_watchdog_refuses_fails_the_task()
    {
        var watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Running(Name));
        watchdog.Calls = _instances.Calls;
        watchdog.RefuseRelease = true;

        var progress = new WindowProgress();
        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog, progress), default);

        Assert.Equal(MaintenanceOutcomes.Failed, outcome.Outcome);
        Assert.Contains("did not bring the instance back", outcome.Message!);
        Assert.False(progress.InstanceCycled);
    }

    /// <summary>
    /// A server nobody wants running needs no park: the engine updates a stopped instance, and it
    /// stays stopped afterwards because that is what desired-state says.
    /// </summary>
    [Fact]
    public async Task A_stopped_instance_is_updated_without_a_park()
    {
        var watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Stopped(Name));
        watchdog.Calls = _instances.Calls;
        watchdog.RefusePark = true;

        var progress = new WindowProgress();
        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog, progress), default);

        Assert.Equal(MaintenanceOutcomes.Ok, outcome.Outcome);
        Assert.Equal([$"park:{Name}:scheduler", $"update:{Name}:system:scheduler:system"], _instances.Calls);
        Assert.False(progress.InstanceCycled);
    }

    /// <summary>
    /// A refused park on an instance the watchdog still holds live is the one case that must not
    /// reach the engine: it would refuse the update anyway, and a supervisor could spawn the server
    /// out of a directory mid-write.
    /// </summary>
    [Fact]
    public async Task An_instance_that_will_not_park_and_is_still_live_is_not_updated()
    {
        var watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Running(Name));
        watchdog.Calls = _instances.Calls;
        watchdog.RefusePark = true;

        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog), default);

        Assert.Equal(MaintenanceOutcomes.Failed, outcome.Outcome);
        Assert.Contains("could not be parked", outcome.Message!);
        Assert.Equal([$"park:{Name}:scheduler"], _instances.Calls);
    }

    /// <summary>
    /// A park that did not take and a watchdog that will not say why measure nothing between them,
    /// and "nothing will spawn it" is not what an unanswered probe says.
    /// </summary>
    [Fact]
    public async Task A_watchdog_that_does_not_answer_leaves_the_update_undone()
    {
        var watchdog = WatchdogClientStub.Unreachable();
        watchdog.Calls = _instances.Calls;
        watchdog.RefusePark = true;

        TaskOutcome outcome = await NewTask().RunAsync(Context(_instances, watchdog), default);

        Assert.Equal(MaintenanceOutcomes.Failed, outcome.Outcome);
        Assert.DoesNotContain(_instances.Calls, c => c.StartsWith("update:", StringComparison.Ordinal));
    }
}
