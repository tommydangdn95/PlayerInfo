using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PlayerInfo.Api.Logging;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Live application log for the admin UI (in-memory only, lost on restart). Requires an admin JWT
/// (fallback policy). The stream uses Server-Sent Events; the UI reads it with fetch() so the
/// Authorization header can be sent.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class LogsController(LiveLogBuffer buffer) : ControllerBase
{
    /// <summary>Heartbeat interval: keeps proxies (Render) from closing an idle stream.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Entries after <paramref name="after"/> (oldest first), at most <paramref name="limit"/>.</summary>
    [HttpGet]
    public IActionResult GetRecent([FromQuery] long after = 0, [FromQuery] int limit = 500)
        => Ok(buffer.GetAfter(after, Math.Clamp(limit, 1, LiveLogBuffer.Capacity)));

    /// <summary>
    /// Server-Sent Events: first the backlog after <paramref name="after"/> (or the Last-Event-ID
    /// header), then every new entry as it is logged, plus a comment heartbeat while idle.
    /// </summary>
    [HttpGet("stream")]
    public async Task StreamAsync([FromQuery] long after = 0, CancellationToken ct = default)
    {
        if (long.TryParse(Request.Headers["Last-Event-ID"], out var lastEventId) && lastEventId > after)
        {
            after = lastEventId;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        using var subscription = buffer.Subscribe(after);

        // Ask the browser to wait 3s before reconnecting on its own.
        await Response.WriteAsync("retry: 3000\n\n", ct);
        foreach (var entry in subscription.Backlog)
        {
            await WriteEntryAsync(entry, ct);
        }

        await Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(HeartbeatInterval);
                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(heartbeat.Token))
                    {
                        break; // unsubscribed
                    }

                    while (subscription.Reader.TryRead(out var entry))
                    {
                        await WriteEntryAsync(entry, ct);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await Response.WriteAsync(": ping\n\n", ct);
                }

                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client closed the page.
        }
    }

    private Task WriteEntryAsync(LiveLogEntry entry, CancellationToken ct)
        => Response.WriteAsync($"id: {entry.Id}\ndata: {JsonSerializer.Serialize(entry, Json)}\n\n", ct);
}
