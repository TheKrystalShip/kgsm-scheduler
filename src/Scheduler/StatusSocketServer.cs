using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Kgsm.Scheduler.Json;

namespace TheKrystalShip.Kgsm.Scheduler;

internal sealed class StatusSocketServer(
    SchedulerOptions options,
    ScheduleRegistry registry,
    ILogger<StatusSocketServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var path = options.StatusSocketPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        logger.LogInformation("Status socket listening on {Path}", path);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Accept error"); continue; }

            // Fire-and-forget per-connection handler
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var snapshot = registry.Snapshot;
                var json = JsonSerializer.Serialize(snapshot, SchedulerJsonContext.Default.SchedulerStatusResponse);
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                await client.SendAsync(bytes, SocketFlags.None, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Status socket client error");
        }
    }
}
