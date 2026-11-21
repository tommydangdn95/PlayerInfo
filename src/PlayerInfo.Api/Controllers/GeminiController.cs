using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Clients;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Gemini usage against the free-tier limits of each model, per Google quota day (midnight Pacific),
/// like Google AI Studio's "Rate limits by model".
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class GeminiController(AppDbContext db, GeminiEnricherClient gemini, IOptions<CollectorOptions> options) : ControllerBase
{
    /// <summary>
    /// A model with its limits (0 = no limit) and today's use: requests, the busiest minute (requests and tokens)
    /// and outcomes. <see cref="Order"/> is its place in the fallback chain, null when it is not used.
    /// </summary>
    public sealed record ModelDto(
        string Model,
        int? Order,
        int RequestsPerDay,
        int RequestsPerMinute,
        int TokensPerMinute,
        int UsedToday,
        int PeakRequestsPerMinute,
        long PeakTokensPerMinute,
        int SuccessToday,
        int RateLimitedToday,
        int OverloadedToday,
        long TokensToday,
        int AvgDurationMs,
        string? LastOutcome,
        DateTime? LastAtUtc);

    public sealed record DayDto(string Day, int Requests, int Success, int Failed, long PromptTokens, long OutputTokens, long TotalTokens);

    public sealed record CallDto(
        DateTime AtUtc, string? Model, int? StatusCode, string Outcome, int DurationMs,
        int PromptTokens, int OutputTokens, int TotalTokens, int ArticleCount, string? JobId);

    public sealed record UsageDto(
        bool Configured,
        int BatchSize,
        string QuotaDay,
        DateTime ResetAtUtc,
        UsageController.ProviderTotals Today,
        IReadOnlyList<ModelDto> Models,
        IReadOnlyList<DayDto> Daily,
        IReadOnlyList<CallDto> Recent);

    [HttpGet("usage")]
    public async Task<IActionResult> GetUsageAsync([FromQuery] int days = 14, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 60);
        var today = GeminiQuotaDay.Today();
        var firstDay = today.AddDays(1 - days);
        var since = GeminiQuotaDay.StartUtc(firstDay);

        var calls = await db.ApiCalls.AsNoTracking()
            .Where(c => c.Provider == ApiUsageRecorder.GeminiProvider && c.AtUtc >= since)
            .OrderByDescending(c => c.AtUtc)
            .ToListAsync(ct);

        var byDay = calls.ToLookup(c => GeminiQuotaDay.Of(c.AtUtc));
        var todayCalls = byDay[today].ToList();
        var opts = options.Value;

        // The fallback chain in order, then any other model with known limits or used today.
        var chain = opts.GeminiModels().ToList();
        var others = opts.GeminiModelLimits.Select(l => l.Model)
            .Concat(todayCalls.Select(c => c.Model ?? "?"))
            .Where(m => !chain.Contains(m, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var models = chain.Select((m, i) => (Model: m, Order: (int?)(i + 1)))
            .Concat(others.Select(m => (Model: m, Order: (int?)null)))
            .Select(x => ToModel(x.Model, x.Order, opts.LimitFor(x.Model),
                todayCalls.Where(c => string.Equals(c.Model, x.Model, StringComparison.OrdinalIgnoreCase)).ToList()))
            .ToList();

        var daily = Enumerable.Range(0, days)
            .Select(i => firstDay.AddDays(i))
            .Select(d =>
            {
                var list = byDay[d].ToList();
                return new DayDto(
                    d.ToString("yyyy-MM-dd"),
                    list.Count,
                    list.Count(c => c.Outcome == ApiUsageRecorder.Success),
                    list.Count(c => c.Outcome != ApiUsageRecorder.Success),
                    list.Sum(c => (long)c.PromptTokens),
                    list.Sum(c => (long)c.OutputTokens),
                    list.Sum(c => (long)c.TotalTokens));
            })
            .ToList();

        return Ok(new UsageDto(
            gemini.IsConfigured,
            opts.GeminiBatchSize,
            today.ToString("yyyy-MM-dd"),
            GeminiQuotaDay.NextResetUtc(),
            UsageController.Sum(todayCalls),
            models,
            daily,
            calls.Take(50)
                .Select(c => new CallDto(c.AtUtc, c.Model, c.StatusCode, c.Outcome, c.DurationMs, c.PromptTokens, c.OutputTokens, c.TotalTokens, c.ArticleCount, c.JobId))
                .ToList()));
    }

    private static ModelDto ToModel(string model, int? order, GeminiModelLimit limit, List<ApiCall> today)
    {
        // Busiest minute of the day, as Google compares against the per-minute limits.
        var minutes = today.GroupBy(c => new DateTime(c.AtUtc.Ticks - c.AtUtc.Ticks % TimeSpan.TicksPerMinute)).ToList();
        var last = today.FirstOrDefault(); // newest first
        return new ModelDto(
            model,
            order,
            limit.RequestsPerDay,
            limit.RequestsPerMinute,
            limit.TokensPerMinute,
            today.Count,
            minutes.Count == 0 ? 0 : minutes.Max(g => g.Count()),
            minutes.Count == 0 ? 0 : minutes.Max(g => g.Sum(c => (long)c.TotalTokens)),
            today.Count(c => c.Outcome == ApiUsageRecorder.Success),
            today.Count(c => c.Outcome == ApiUsageRecorder.RateLimited),
            today.Count(c => c.Outcome == ApiUsageRecorder.Overloaded),
            today.Sum(c => (long)c.TotalTokens),
            today.Count == 0 ? 0 : (int)today.Average(c => c.DurationMs),
            last?.Outcome,
            last?.AtUtc);
    }
}
