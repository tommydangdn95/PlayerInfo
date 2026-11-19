using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Background;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Security;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class JobsController(JobQueue queue, QuotaManager quota, ILogger<JobsController> logger) : ControllerBase
{
    /// <summary>
    /// Triggers the collector pipeline asynchronously and returns 202 Accepted immediately.
    /// </summary>
    [HttpPost("run")]
    [Authorize(Policy = Policies.AdminOrCron)]
    public async Task<IActionResult> RunAsync([FromQuery] string mode = "news", CancellationToken ct = default)
    {
        if (!TryParseMode(mode, out var parsedMode))
        {
            return BadRequest(new { error = "Invalid mode. Allowed: full, transfers_news, news, roster." });
        }

        var jobId = JobRequest.NewJobId();
        await queue.EnqueueAsync(new JobRequest(parsedMode, DateTime.UtcNow, jobId), ct);
        logger.LogInformation("Queued job {JobId} mode={Mode} by {User}", jobId, parsedMode, HttpContext?.User?.Identity?.Name ?? "cron");
        return Accepted(new
        {
            queued = true,
            jobId,
            mode = parsedMode.ToString(),
            queuedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Returns today's API request usage from ApiQuotaLogs.
    /// </summary>
    [HttpGet("quota")]
    public async Task<IActionResult> GetQuotaAsync(
        [FromServices] IOptions<CollectorOptions> options,
        CancellationToken ct)
    {
        var apiFootball = await quota.GetTodayUsageAsync(QuotaManager.ApiFootballProvider, ct);
        var newsData = await quota.GetTodayUsageAsync(QuotaManager.NewsDataProvider, ct);
        return Ok(new
        {
            dateUtc = DateOnly.FromDateTime(DateTime.UtcNow),
            apiFootball,
            newsData,
            apiFootballLimit = options.Value.ApiFootballDailyLimit,
            newsDataLimit = options.Value.NewsDataDailyLimit
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
