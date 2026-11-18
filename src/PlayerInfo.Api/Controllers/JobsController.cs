using Microsoft.AspNetCore.Mvc;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Security;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class JobsController(JobQueue queue, QuotaManager quota) : ControllerBase
{
    /// <summary>
    /// Triggers the collector pipeline asynchronously and returns 202 Accepted immediately.
    /// </summary>
    [HttpPost("run")]
    [RequireCronSecret]
    public async Task<IActionResult> RunAsync([FromQuery] string mode = "news", CancellationToken ct = default)
    {
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
        var apiFootball = await quota.GetTodayUsageAsync(QuotaManager.ApiFootballProvider, ct);
        var newsData = await quota.GetTodayUsageAsync(QuotaManager.NewsDataProvider, ct);
        return Ok(new
        {
            dateUtc = DateOnly.FromDateTime(DateTime.UtcNow),
            apiFootball,
            newsData
        });
    }

    private static bool TryParseMode(string? mode, out CollectorMode parsed)
    {
        CollectorMode? result = mode?.Trim().ToLowerInvariant() switch
        {
            "full" => CollectorMode.Full,
            "transfers_news" => CollectorMode.TransfersNews,
            "news" => CollectorMode.News,
            "roster" => CollectorMode.Roster,
            _ => null
        };

        parsed = result.GetValueOrDefault();
        return result.HasValue;
    }
}
