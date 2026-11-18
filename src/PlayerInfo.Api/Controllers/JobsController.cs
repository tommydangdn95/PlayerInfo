using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class JobsController(
    JobQueue queue,
    QuotaManager quota,
    IOptions<CronOptions> cronOptions) : ControllerBase
{
    /// <summary>
    /// Triggers the collector pipeline asynchronously and returns 202 Accepted immediately.
    /// </summary>
    [HttpPost("run")]
    public async Task<IActionResult> RunAsync(
        [FromQuery] string mode = "news",
        [FromHeader(Name = "X-Cron-Secret")] string? secret = null,
        CancellationToken ct = default)
    {
        var expected = cronOptions.Value.SecretKey;
        if (!string.IsNullOrEmpty(expected) && !ConstantTimeEquals(secret, expected))
        {
            return Unauthorized(new { error = "Invalid X-Cron-Secret header." });
        }

        if (!TryParseMode(mode, out var parsedMode))
        {
            return BadRequest(new { error = "Invalid mode. Allowed: full, transfers_news, news, roster." });
        }

        await queue.EnqueueAsync(new JobRequest(parsedMode, DateTime.UtcNow), ct);
        return Accepted(new
        {
            queued = true,
            mode = parsedMode.ToString(),
            queuedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns today's API request usage from ApiQuotaLogs.
    /// </summary>
    [HttpGet("quota")]
    public async Task<IActionResult> GetQuotaAsync(CancellationToken ct)
    {
        var apiFootball = await quota.GetTodayUsageAsync("ApiFootball", ct);
        var newsData = await quota.GetTodayUsageAsync("NewsData", ct);
        return Ok(new
        {
            dateUtc = DateOnly.FromDateTime(DateTime.UtcNow),
            apiFootball,
            newsData
        });
    }

    private static bool TryParseMode(string mode, out CollectorMode parsed)
    {
        parsed = mode.Trim().ToLowerInvariant() switch
        {
            "full" => CollectorMode.Full,
            "transfers_news" => CollectorMode.TransfersNews,
            "news" => CollectorMode.News,
            "roster" => CollectorMode.Roster,
            _ => (CollectorMode)(-1)
        };
        return Enum.IsDefined(parsed);
    }

    private static bool ConstantTimeEquals(string? a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
