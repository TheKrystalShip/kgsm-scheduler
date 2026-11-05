using TheKrystalShip.KGSM.Engine.Contracts.Backups;
using TheKrystalShip.KGSM.Engine.Contracts.Invocation;
using TheKrystalShip.KGSM.Engine.Reading;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// The writes the daemon makes carry its own provenance, so the audit trail reads as the scheduler and
/// never as whoever owns the process.
/// </summary>
public sealed class EngineGatewayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kgsm-sched-gateway-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class RecordedProcess : IEngineProcess
    {
        public List<string> Arguments { get; } = [];

        public Task<EngineProcessResult> RunAsync(
            string binary, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            string? standardInput, CancellationToken cancellationToken)
        {
            Arguments.AddRange(arguments);
            return Task.FromResult(new EngineProcessResult(1, "", "{\"code\":\"failed\",\"message\":\"no\",\"exitCode\":1,\"details\":[]}"));
        }

        public Task<EngineStreamResult> RunStreamingAsync(
            string binary, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
            Stream? source, Stream? destination, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private EngineGateway Gateway(RecordedProcess process) => new(
        new EngineReader(new EngineReaderOptions
        {
            InstallRoot = Path.Combine(_root, "install"),
            HomeFallback = Path.Combine(_root, "home"),
            Environment = new Dictionary<string, string?>
            {
                ["HOME"] = Path.Combine(_root, "home"),
                ["XDG_CONFIG_HOME"] = Path.Combine(_root, "config"),
                ["XDG_DATA_HOME"] = Path.Combine(_root, "data"),
                ["XDG_STATE_HOME"] = Path.Combine(_root, "state"),
            },
        }),
        new EngineCli(new EngineCliOptions { BinaryPath = "kgsm" }, process));

    [Fact]
    public async Task EveryWriteNamesTheSchedulerAsActorAndSystemAsOrigin()
    {
        var process = new RecordedProcess();
        EngineGateway gateway = Gateway(process);

        await gateway.CreateBackupAsync("mc-01", BackupReason.Scheduled, CancellationToken.None);
        await gateway.AnnounceAsync("mc-01", "back soon", CancellationToken.None);

        Assert.Equal(2, process.Arguments.Count(a => a == "--actor"));
        Assert.Equal(2, process.Arguments.Count(a => a == "system:scheduler"));
        Assert.Equal(2, process.Arguments.Count(a => a == "--origin"));
        Assert.Equal(2, process.Arguments.Count(a => a == "system"));
        Assert.Contains("scheduled", process.Arguments);
    }

    [Fact]
    public async Task ARefusalTravelsAsTheEnginesMessage()
    {
        var result = await Gateway(new RecordedProcess()).UpdateAsync("mc-01", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("no", result.Message);
    }

    [Fact]
    public void AnEmptyHostHasNoInstancesAndNoVersions()
    {
        EngineGateway gateway = Gateway(new RecordedProcess());

        Assert.Empty(gateway.Instances()!);
        Assert.Empty(gateway.RecordedVersions());
    }
}
