using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// What the clock is allowed to act on. The gate stands between a window coming due and the
/// watchdog being told to restart something, and the whole of its job is refusing to act on an
/// instance whose state does not warrant it — while keeping the reason it refused distinguishable
/// from every other reason.
/// </summary>
public class RestartGateTests
{
    private static WatchdogInstanceState State(string phase, bool populated = true,
        int restarts = 0, string reason = "") =>
        new()
        {
            Name = "factorio-01",
            Desired = "running",
            Phase = phase,
            Populated = populated,
            Restarts = restarts,
            Reason = reason,
        };

    private static Task<TaskGate> Evaluate(
        WatchdogClientStub watchdog, InstanceRuntime? runtime = InstanceRuntime.Native) =>
        RestartGate.EvaluateAsync(watchdog, "factorio-01", runtime, CancellationToken.None);

    // ---- what the gate lets through ----------------------------------------

    [Fact]
    public async Task A_running_instance_is_restarted()
    {
        var decision = await Evaluate(WatchdogClientStub.Answering(State("running")));

        Assert.Equal(TaskGateOutcome.Dispatch, decision.Outcome);
    }

    // ---- what it abandons --------------------------------------------------

    // An operator start clears the give-up latch and the failure streak, so dispatching here would
    // wipe a crash-looping instance's failure history on a timer and hand it back to the supervisor
    // as healthy.
    [Fact]
    public async Task An_instance_the_watchdog_gave_up_on_is_left_alone()
    {
        var decision = await Evaluate(WatchdogClientStub.Answering(
            State("failed", populated: false, restarts: 5, reason: "crashed (exit 139)")));

        Assert.Equal(TaskGateOutcome.Skip, decision.Outcome);
        Assert.Contains("gave up", decision.Message);
        Assert.Contains("5 failed restart(s)", decision.Message);
        Assert.Contains("crashed (exit 139)", decision.Message);
    }

    // A stop of an untracked instance succeeds as a no-op, so an ungated restart would run into its
    // start half and spawn a server somebody deliberately stopped.
    [Fact]
    public async Task An_instance_the_watchdog_does_not_track_is_not_started()
    {
        var decision = await Evaluate(WatchdogClientStub.Answering(null));

        Assert.Equal(TaskGateOutcome.Skip, decision.Outcome);
        Assert.Contains("not supervising", decision.Message);
    }

    // A pending crash-restart is the watchdog still handling the instance, and two supervisors acting
    // on one process is the boundary this daemon is bound by.
    [Theory]
    [InlineData("restart-pending")]
    [InlineData("stopped")]
    [InlineData("unknown")]
    public async Task Any_phase_short_of_running_is_abandoned(string phase)
    {
        var decision = await Evaluate(WatchdogClientStub.Answering(State(phase)));

        Assert.Equal(TaskGateOutcome.Skip, decision.Outcome);
        Assert.Contains(phase, decision.Message);
    }

    // The phase is what the supervisor intends; the cgroup is what the kernel measures. Only the
    // second is evidence that there is a process to restart.
    [Fact]
    public async Task A_running_phase_over_an_empty_cgroup_is_abandoned()
    {
        var decision = await Evaluate(WatchdogClientStub.Answering(State("running", populated: false)));

        Assert.Equal(TaskGateOutcome.Skip, decision.Outcome);
        Assert.Contains("cgroup is empty", decision.Message);
    }

    // ---- an unknown is never reported as a state ---------------------------

    [Fact]
    public async Task An_unreachable_watchdog_abandons_without_claiming_a_state()
    {
        var decision = await Evaluate(WatchdogClientStub.Unreachable());

        Assert.Equal(TaskGateOutcome.Fail, decision.Outcome);
        Assert.Contains("did not answer", decision.Message);
        Assert.Contains("unknown", decision.Message);
        // The one thing it must never say: that the instance is not running. Nothing measured it.
        Assert.DoesNotContain("not running", decision.Message);
        Assert.DoesNotContain("not supervising", decision.Message);
    }

    // The two abandonments a surface most needs to tell apart: a daemon that could not be asked, and
    // a daemon that answered and does not track the instance.
    [Fact]
    public async Task Unreachable_and_untracked_read_differently()
    {
        var unreachable = await Evaluate(WatchdogClientStub.Unreachable());
        var untracked = await Evaluate(WatchdogClientStub.Answering(null));

        Assert.NotEqual(unreachable.Message, untracked.Message);
        Assert.NotEqual(unreachable.Outcome, untracked.Outcome);
    }

    // ---- how a skip is recorded --------------------------------------------

    // A restart owed and not delivered is a failure a surface should raise. A restart that does not
    // apply is not: the clock came round for a server that is deliberately down, and declining is
    // the right outcome — recorded, with its reason, and not as a red row.
    [Fact]
    public async Task An_owed_restart_fails_and_an_inapplicable_one_skips()
    {
        var blocked = await Evaluate(WatchdogClientStub.Unreachable());
        var notApplicable = await Evaluate(WatchdogClientStub.Answering(null));

        Assert.Equal(TaskGateOutcome.Fail, blocked.Outcome);
        Assert.Equal(TaskGateOutcome.Skip, notApplicable.Outcome);
    }

    // ---- runtimes the watchdog does not supervise --------------------------

    // The watchdog answers 404 for a container exactly as it does for a stopped native instance, so
    // a restart that can never be honoured would otherwise report itself as a server that happened
    // to be down.
    [Fact]
    public async Task A_container_instance_is_reported_as_out_of_the_watchdogs_scope()
    {
        var watchdog = WatchdogClientStub.Answering(null);

        var decision = await Evaluate(watchdog, InstanceRuntime.Container);

        // Declined, not failed: nothing was owed, and the rest of the window still runs — which is
        // what keeps a container's nightly archive from being lost to the restart beside it.
        Assert.Equal(TaskGateOutcome.Skip, decision.Outcome);
        Assert.Contains("container instance", decision.Message);
        // Decided from the runtime alone; the socket is never dialed for one.
        Assert.Equal(0, watchdog.StatusCalls);
    }

    // An unreported runtime is not assumed to be anything. The watchdog is asked, and its answer —
    // supervised or not — decides, so a native instance whose config could not be read is neither
    // restarted blind nor written off.
    [Fact]
    public async Task An_unreported_runtime_is_decided_by_asking_the_watchdog()
    {
        var running = WatchdogClientStub.Answering(State("running"));
        var absent = WatchdogClientStub.Answering(null);

        Assert.Equal(TaskGateOutcome.Dispatch, (await Evaluate(running, runtime: null)).Outcome);
        Assert.Equal(TaskGateOutcome.Skip, (await Evaluate(absent, runtime: null)).Outcome);
        Assert.Equal(1, running.StatusCalls);
    }
}
