using System.Text.Json.Serialization;

namespace TheKrystalShip.Kgsm.Scheduler.Json;

[JsonSerializable(typeof(SchedulerStatusResponse))]
[JsonSerializable(typeof(IReadOnlyList<SchedulerInstanceStatus>))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(Dictionary<string, PendingAnnouncement>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SchedulerJsonContext : JsonSerializerContext { }
