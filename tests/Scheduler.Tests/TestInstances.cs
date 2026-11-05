using TheKrystalShip.KGSM.Engine.Contracts;
using TheKrystalShip.KGSM.Engine.Contracts.Blueprints;
using TheKrystalShip.KGSM.Engine.Contracts.Instances;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>An instance config with every required key set, for a test to reshape with <c>with</c>.</summary>
internal static class TestInstances
{
    public static InstanceConfig Config(string name = "factorio-01") => new()
    {
        SchemaVersion = SchemaVersions.InstanceConfig,
        Name = name,
        DisplayName = string.Empty,
        Blueprint = "factorio",
        Runtime = BlueprintRuntime.Native,
        InstallNonce = "nonce-" + name,
        BackupsDirectory = "/nonexistent/backups/" + name,
        Platform = "linux",
        AutoUpdateBeforeStart = false,
        CpuPriority = "normal",
        MemoryCapMb = 0,
        MaintenanceWindows = string.Empty,
        Timezone = string.Empty,
        BackupRetention = 3,
        AnnounceLeadMinutes = string.Empty,
        AnnounceMaintenanceMessage = "Server restart in {minutes} min",
        AnnounceMaintenanceCancelledMessage = "Server restart cancelled",
        CrashRestart = true,
        CrashMaxRestarts = 5,
        StartupSuccessRegex = string.Empty,
        PlayerJoinedRegex = string.Empty,
        PlayerLeftRegex = string.Empty,
        RconPassword = string.Empty,
        RconPollIntervalSeconds = 10,
        RconPlayersCommand = string.Empty,
        RconPlayersRegex = string.Empty,
        LevelName = "world",
        ExecutableFile = "server",
        ExecutableSubdirectory = string.Empty,
        ExecutableArguments = string.Empty,
        SteamAppId = 0,
        ClientSteamAppId = 0,
        SteamcmdArguments = string.Empty,
        IsSteamAccountRequired = false,
        Ports = string.Empty,
        EnableFirewallManagement = false,
        EnablePortForwarding = false,
        DownloadTimeoutSeconds = 60,
        StopCommand = string.Empty,
        SaveCommand = string.Empty,
        SaveCommandTimeoutSeconds = 5,
        StopCommandTimeoutSeconds = 30,
        KickCommand = string.Empty,
        BanCommand = string.Empty,
        UnbanCommand = string.Empty,
        BroadcastCommand = string.Empty,
        CompressBackups = true,
    };
}
