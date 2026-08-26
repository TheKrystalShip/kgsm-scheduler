using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Models.Enums;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// One window run: what it does, in what order, and — above all — which window the record lands on.
/// </summary>
public sealed class MaintenanceRunnerTests : IDisposable
{
    private const string Name = "factorio-01";

    private readonly string _dir = Directory.CreateTempSubdirectory("kgsm-sched-runner-").FullName;
    private readonly InstanceServiceStub _instances = new();
    private readonly WatchdogClientStub _watchdog = WatchdogClientStub.Answering(WatchdogClientStub.Running(Name));
    private readonly ScheduleRegistry _registry = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly MaintenanceTaskCatalog Catalog = new(
        [new BackupTask(NullLogger<BackupTask>.Instance), new RestartTask(NullLogger<RestartTask>.Instance)]);

    private static Instance NewInstance(InstanceRuntime runtime = InstanceRuntime.Native) => new()
    {
        Name = Name,
        DisplayName = "Factorio",
        Runtime = runtime,
        BackupRetention = 5,
    };

    private static ReadWindow Read(string expression) =>
        WindowPlanner.Read(MaintenanceWindowParser.ParseWindow(expression), Catalog, 10,
            TimeZoneInfo.Utc, DateTime.UtcNow);

    private MaintenanceRunner NewRunner(bool allowDisruptive = true)
    {
        var options = Options.Create(new SchedulerOptions { AllowDisruptiveTasks = allowDisruptive });
        var announcer = new WindowAnnouncer(
            _instances, _watchdog,
            new PendingAnnouncementStore(_dir, NullLogger.Instance),
            NullLogger<WindowAnnouncer>.Instance);

        return new MaintenanceRunner(_instances, _watchdog, _registry, Catalog, announcer, options,
            NullLogger<MaintenanceRunner>.Instance);
    }

    /// <summary>Seeds the registry the way a tick does, so a run has a window to record against.</summary>
    private void Plan(params string[] expressions)
    {
        var windows = new Dictionary<string, WindowState>(StringComparer.Ordinal);
        foreach (string expression in expressions)
        {
            ReadWindow read = Read(expression);
            windows[read.Window.Id] = new WindowState(
                read.Window, TimeZoneInfo.Utc, new WindowPlan("seed", DateTime.UtcNow), null);
        }

        _registry.Set(Name, new ScheduleState { Windows = windows });
    }

    private async Task<MaintenanceRun?> RunAsync(MaintenanceRunner runner, string expression)
    {
        ReadWindow read = Read(expression);
        runner.Fire(Name, NewInstance(), read);

        for (int i = 0; i < 200; i++)
        {
            MaintenanceRun? run = _registry.Get(Name)?.Windows.GetValueOrDefault(read.Window.Id)?.LastRun;
            if (run is not null) return run;
            await Task.Delay(10);
        }

        return null;
    }

    // ---- what a window does ------------------------------------------------

    [Fact]
    public async Task A_backup_window_archives_and_prunes()
    {
        Plan("daily@05:00/backup");

        MaintenanceRun? run = await RunAsync(NewRunner(), "daily@05:00/backup");

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Equal([("backup", MaintenanceOutcomes.Ok)], run.Tasks.Select(t => (t.Name, t.Outcome)));
        Assert.Equal([$"backup:{Name}:scheduled:system:scheduler:system", $"prune:{Name}:5"], _instances.Calls);
    }

    [Fact]
    public async Task A_restart_window_bounces_the_instance_through_the_watchdog()
    {
        Plan("daily@04:00/restart");

        MaintenanceRun? run = await RunAsync(NewRunner(), "daily@04:00/restart");

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Equal([Name], _watchdog.Restarts);
    }

    // Canonical order, whatever order the window was written in: a backup taken after an update
    // archives the new build instead of the rollback point.
    [Fact]
    public async Task Tasks_run_in_canonical_order()
    {
        Plan("daily@04:00/restart,backup");

        MaintenanceRun? run = await RunAsync(NewRunner(), "daily@04:00/restart,backup");

        Assert.Equal(["backup", "restart"], run!.Tasks.Select(t => t.Name));
    }

    // ---- a failure abandons the rest ---------------------------------------

    [Fact]
    public async Task The_first_failure_aborts_the_rest_of_the_window()
    {
        _instances.BackupExitCode = 1;
        _instances.BackupStderr = "no space left on device";
        Plan("daily@04:00/backup,restart");

        MaintenanceRun? run = await RunAsync(NewRunner(), "daily@04:00/backup,restart");

        Assert.Equal(MaintenanceOutcomes.Failed, run!.Outcome);
        Assert.Equal(
            [("backup", MaintenanceOutcomes.Failed), ("restart", MaintenanceOutcomes.Aborted)],
            run.Tasks.Select(t => (t.Name, t.Outcome)));
        // A partially-run window is worse than a skipped one: the restart never reaches the watchdog.
        Assert.Empty(_watchdog.Restarts);
        Assert.Contains("no space left", run.Tasks[0].Message);
    }

    // The archive is the job and it landed, so a rotation that refused is not a failed backup. It is
    // also not nothing, so it travels on the record.
    [Fact]
    public async Task A_failed_prune_does_not_fail_the_backup()
    {
        _instances.PruneExitCode = 1;
        Plan("daily@05:00/backup");

        MaintenanceRun? run = await RunAsync(NewRunner(), "daily@05:00/backup");

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Contains("prune failed", run.Tasks[0].Message);
    }

    // ---- what does not apply -----------------------------------------------

    [Fact]
    public async Task A_task_the_gate_declines_is_skipped_and_the_window_carries_on()
    {
        var runner = new MaintenanceRunner(
            _instances, WatchdogClientStub.Answering(null), _registry, Catalog,
            new WindowAnnouncer(_instances, _watchdog,
                new PendingAnnouncementStore(_dir, NullLogger.Instance), NullLogger<WindowAnnouncer>.Instance),
            Options.Create(new SchedulerOptions()), NullLogger<MaintenanceRunner>.Instance);

        Plan("daily@04:00/backup,restart");

        MaintenanceRun? run = await RunAsync(runner, "daily@04:00/backup,restart");

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Equal(
            [("backup", MaintenanceOutcomes.Ok), ("restart", MaintenanceOutcomes.Skipped)],
            run.Tasks.Select(t => (t.Name, t.Outcome)));
        Assert.Contains("not supervising", run.Tasks[1].Message);
    }

    // A host policy is a deliberate choice, not a misconfiguration, so it is recorded as a skip and
    // never as a failure — and it does not abort the tasks after it.
    [Fact]
    public async Task Disruptive_tasks_are_skipped_where_the_host_does_not_permit_them()
    {
        Plan("daily@04:00/backup,restart");

        MaintenanceRun? run = await RunAsync(NewRunner(allowDisruptive: false), "daily@04:00/backup,restart");

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Equal(MaintenanceOutcomes.Skipped, run.Tasks[1].Outcome);
        Assert.Contains("not permitted", run.Tasks[1].Message);
        Assert.Empty(_watchdog.Restarts);
    }

    /// <summary>
    /// A countdown that can only end in a retraction should never open. Both things that put a
    /// restart out of reach — the host policy, and a runtime the watchdog does not supervise — are
    /// known before the first mark comes due.
    /// </summary>
    [Fact]
    public void A_window_with_nothing_that_can_disturb_anybody_is_not_announced()
    {
        ReadWindow window = Read("daily@04:00/backup,restart");

        Assert.Empty(NewRunner(allowDisruptive: false).DisruptiveTasks(window, NewInstance()));
        Assert.Empty(NewRunner().DisruptiveTasks(window, NewInstance(InstanceRuntime.Container)));
        Assert.Empty(NewRunner().DisruptiveTasks(Read("daily@05:00/backup"), NewInstance()));
        Assert.Equal([MaintenanceTask.Restart], NewRunner().DisruptiveTasks(window, NewInstance()));
    }

    /// <summary>
    /// The watchdog supervises native instances alone, so a container's restart was never going to
    /// happen and nothing about it is owed. Declining rather than failing is what leaves the archive
    /// written beside it still firing.
    /// </summary>
    [Fact]
    public async Task A_containers_restart_is_declined_without_losing_its_backup()
    {
        Plan("daily@04:00/backup,restart");
        ReadWindow read = Read("daily@04:00/backup,restart");
        NewRunner().Fire(Name, NewInstance(InstanceRuntime.Container), read);

        MaintenanceRun? run = null;
        for (int i = 0; i < 200 && run is null; i++)
        {
            run = _registry.Get(Name)?.Windows["daily@04:00"].LastRun;
            if (run is null) await Task.Delay(10);
        }

        Assert.Equal(MaintenanceOutcomes.Ok, run!.Outcome);
        Assert.Equal(
            [("backup", MaintenanceOutcomes.Ok), ("restart", MaintenanceOutcomes.Skipped)],
            run.Tasks.Select(t => (t.Name, t.Outcome)));
        Assert.Contains("container instance", run.Tasks[1].Message);
        Assert.Empty(_watchdog.Restarts);
    }

    // ---- one window at a time, recorded against itself ---------------------

    /// <summary>
    /// The record belongs to the window that was skipped, and to no other. An instance's windows are
    /// independent appointments, so a backup still running must never be reported in the restart's
    /// fields — nor as a failure, since nothing failed.
    /// </summary>
    [Fact]
    public async Task A_window_that_finds_the_instance_busy_is_skipped_against_itself()
    {
        using var hold = new SemaphoreSlim(0, 1);
        _instances.HoldBackup = hold;
        Plan("daily@05:00/backup", "daily@04:00/restart");

        MaintenanceRunner runner = NewRunner();
        runner.Fire(Name, NewInstance(), Read("daily@05:00/backup"));

        // Wait for the backup to be in flight, then bring the restart window round on top of it.
        for (int i = 0; i < 200 && _instances.Calls.Count == 0; i++) await Task.Delay(10);
        runner.Fire(Name, NewInstance(), Read("daily@04:00/restart"));

        MaintenanceRun? restart = _registry.Get(Name)!.Windows["daily@04:00"].LastRun;

        Assert.NotNull(restart);
        Assert.Equal(MaintenanceOutcomes.Skipped, restart.Outcome);
        Assert.Equal("restart", Assert.Single(restart.Tasks).Name);
        Assert.Contains("already running", restart.Tasks[0].Message);

        // And the window that was actually running is untouched by the collision.
        Assert.Null(_registry.Get(Name)!.Windows["daily@05:00"].LastRun);
        Assert.Empty(_watchdog.Restarts);

        hold.Release();
        for (int i = 0; i < 200 && _registry.Get(Name)!.Windows["daily@05:00"].LastRun is null; i++)
            await Task.Delay(10);
        Assert.Equal(MaintenanceOutcomes.Ok, _registry.Get(Name)!.Windows["daily@05:00"].LastRun!.Outcome);
    }

    // ---- how the window as a whole reads -----------------------------------

    [Fact]
    public void A_failure_anywhere_makes_the_window_a_failure() =>
        Assert.Equal(MaintenanceOutcomes.Failed, MaintenanceRunner.Verdict([
            new MaintenanceTaskRun("backup", MaintenanceOutcomes.Ok, null),
            new MaintenanceTaskRun("restart", MaintenanceOutcomes.Failed, "no"),
        ]));

    [Fact]
    public void One_task_that_did_its_work_makes_the_window_ok() =>
        Assert.Equal(MaintenanceOutcomes.Ok, MaintenanceRunner.Verdict([
            new MaintenanceTaskRun("backup", MaintenanceOutcomes.Ok, null),
            new MaintenanceTaskRun("restart", MaintenanceOutcomes.Skipped, "stopped"),
        ]));

    // A window where nothing applied is a different sentence from one that tried and could not.
    [Fact]
    public void A_window_where_nothing_applied_is_skipped() =>
        Assert.Equal(MaintenanceOutcomes.Skipped, MaintenanceRunner.Verdict([
            new MaintenanceTaskRun("restart", MaintenanceOutcomes.Skipped, "stopped"),
        ]));
}
