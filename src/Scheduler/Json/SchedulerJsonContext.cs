using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Scheduler.Json;

[JsonSerializable(typeof(SchedulerStatusResponse))]
[JsonSerializable(typeof(IReadOnlyList<SchedulerInstanceStatus>))]
[JsonSerializable(typeof(SchedulerInstanceStatus))]
[JsonSerializable(typeof(IReadOnlyList<SchedulerWindowStatus>))]
[JsonSerializable(typeof(SchedulerWindowStatus))]
[JsonSerializable(typeof(MaintenanceRun))]
[JsonSerializable(typeof(IReadOnlyList<MaintenanceTaskRun>))]
[JsonSerializable(typeof(MaintenanceTaskRun))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(Dictionary<string, PendingAnnouncement>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SchedulerJsonContext : JsonSerializerContext { }
