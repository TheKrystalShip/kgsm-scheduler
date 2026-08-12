using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TheKrystalShip.Kgsm.Scheduler.Json;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>One instruction to the scheduler: a verb, what it acts on, and however much it needs.</summary>
/// <param name="Command">The verb. Only <c>postpone</c> exists.</param>
/// <param name="Instance">The server whose schedule it acts on.</param>
/// <param name="Minutes">How far to push the next fire back.</param>
internal sealed record ControlRequest(string? Command, string? Instance, int? Minutes);

/// <summary>What came of it. <see cref="NextFireUtc"/> is the schedule as it now stands, so a caller
/// never has to ask again to find out what it just did.</summary>
internal sealed record ControlResponse(bool Ok, string Message, DateTimeOffset? NextFireUtc = null);

/// <summary>
/// The socket the scheduler can be <em>told</em> something on, as opposed to the status socket it
/// answers questions on.
/// </summary>
/// <remarks>
/// <para>
/// <b>A second socket rather than a second use of the first.</b> The status socket's contract is that a
/// client connects and reads one line — it never writes — and everything that reads it depends on that.
/// Teaching it to wait for an optional request first would put a timeout in front of every status read
/// to serve a command that arrives rarely.
/// </para>
/// <para>
/// <b>It stays consumer-agnostic.</b> The scheduler does not know what a Control Panel is; this is a verb
/// any local client can send, and the one that motivated it — deferring tonight's restart from a phone —
/// is one caller among possible others.
/// </para>
/// <para>
/// <b>Postponing moves the standing target, it does not edit the schedule.</b> The instance's
/// configuration is untouched, so the fire after this one lands where it always would have. That is what
/// makes it a postponement rather than a schedule change, and it is why it needs nothing from kgsm.
/// </para>
/// </remarks>
internal sealed class ControlSocketServer(
    IOptions<SchedulerOptions> options,
    ScheduleRegistry registry,
    ILogger<ControlSocketServer> logger) : BackgroundService
{
    /// <summary>The most a single instruction may defer a fire. A postponement is "not right now"; past
    /// this it is a schedule change, and a schedule change belongs in the instance's own config where it
    /// survives a restart of this daemon.</summary>
    internal const int MaxMinutes = 12 * 60;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string path = options.Value.ControlSocketPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(8);
        logger.LogInformation("Control socket listening on {Path}", path);

        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Accept error"); continue; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                // A caller that connects and says nothing must not hold a handler open forever.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));

                string? line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                ControlResponse response = Handle(line);

                byte[] bytes = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(response, SchedulerJsonContext.Default.ControlResponse) + "\n");
                await client.SendAsync(bytes, SocketFlags.None, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Control socket client error");
        }
    }

    internal ControlResponse Handle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return new ControlResponse(false, "empty request");

        ControlRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, SchedulerJsonContext.Default.ControlRequest);
        }
        catch (JsonException)
        {
            return new ControlResponse(false, "malformed request");
        }

        if (request is null) return new ControlResponse(false, "malformed request");

        return request.Command switch
        {
            "postpone" => Postpone(request),
            null or "" => new ControlResponse(false, "no command given"),
            _ => new ControlResponse(false, $"unknown command '{request.Command}'"),
        };
    }

    private ControlResponse Postpone(ControlRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Instance))
            return new ControlResponse(false, "no instance named");

        int minutes = request.Minutes ?? 60;
        if (minutes <= 0 || minutes > MaxMinutes)
            return new ControlResponse(false, $"minutes must be between 1 and {MaxMinutes}");

        // Read-modify-write under the registry's lock, so a tick landing in the middle cannot overwrite
        // the new target with the one it read a moment ago.
        DateTime? moved = null;
        bool hadPlan = false;

        registry.Update(request.Instance, state =>
        {
            if (state.Restart is not { NextUtc: { } next } plan) return state;
            hadPlan = true;
            moved = next.AddMinutes(minutes);
            return state with { Restart = plan with { NextUtc = moved } };
        });

        if (!hadPlan)
            return new ControlResponse(false, $"{request.Instance} has no scheduled restart to postpone");

        logger.LogInformation("{Instance}: scheduled restart postponed {Minutes}min → {Next:o}",
            request.Instance, minutes, moved);

        return new ControlResponse(true, $"postponed {minutes} minute(s)",
            new DateTimeOffset(moved!.Value, TimeSpan.Zero));
    }
}
