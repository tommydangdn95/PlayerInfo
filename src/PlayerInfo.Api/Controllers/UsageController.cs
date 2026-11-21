using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Controllers;

/// <summary>
/// Usage of every external API (from ApiCalls): per provider today, by hour of the day, per day, and the latest calls.
/// Days and hours are Vietnam time.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class UsageController(AppDbContext db, QuotaManager quota, IOptions<CollectorOptions> options) : ControllerBase
{
    /// <summary>Providers in display order (names used by the HTTP trace handler).</summary>
    public static readonly IReadOnlyList<string> Providers = ["ESPN", "RSS", "API-Football", "NewsData", "Gemini", "Telegram"];

    public sealed record ProviderTotals(
        int Calls, int Success, int Failed, int RateLimited, int Timeouts,
        int AvgDurationMs, int MaxDurationMs, long ResponseBytes, long TotalTokens, long PromptTokens, long OutputTokens);

    public sealed record ProviderToday(
        string Provider,
        ProviderTotals Totals,
        int[] CallsByHour,
        int[] FailedByHour,
        DateTime? LastAtUtc,
        string? LastOutcome,
        int? QuotaUsed,
        int? QuotaLimit);

    public sealed record ProviderDay(string Day, int Calls, int Failed, long TotalTokens);

    public sealed record ProviderHistory(string Provider, IReadOnlyList<ProviderDay> Days);

    public sealed record CallDto(
        DateTime AtUtc, string Provider, string? Model, string Endpoint, int? StatusCode, string Outcome,
        int DurationMs, long? ResponseBytes, int TotalTokens, string? JobId);

    public sealed record UsageDto(
        string Day,
        ProviderTotals Today,
        int[] CallsByHour,
        IReadOnlyList<ProviderToday> Providers,
        IReadOnlyList<ProviderHistory> History,
        IReadOnlyList<CallDto> Recent);

    [HttpGet]
    public async Task<IActionResult> GetAsync([FromQuery] string? day = null, [FromQuery] int days = 14, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, ApiUsageRecorder.MaxDays);
        var date = VnDay.ParseOrToday(day);
        var firstDay = date.AddDays(1 - days);
        var (since, _) = VnDay.UtcRange(firstDay);
        var (dayStart, dayEnd) = VnDay.UtcRange(date);

        var calls = await db.ApiCalls.AsNoTracking()
            .Where(c => c.AtUtc >= since && c.AtUtc < dayEnd)
            .OrderByDescending(c => c.AtUtc)
            .ToListAsync(ct);
        var dayCalls = calls.Where(c => c.AtUtc >= dayStart).ToList();

        // API-Football and NewsData keep their own daily counters (UTC day), which also guard their quotas.
        var isToday = date == VnDay.Today();
        var quotas = new Dictionary<string, (int Used, int Limit)>
        {
            ["API-Football"] = (await quota.GetTodayUsageAsync(QuotaManager.ApiFootballProvider, ct), options.Value.ApiFootballDailyLimit),
            ["NewsData"] = (await quota.GetTodayUsageAsync(QuotaManager.NewsDataProvider, ct), options.Value.NewsDataDailyLimit)
        };

        var providers = Providers
            .Concat(dayCalls.Select(c => c.Provider).Distinct().Where(p => !Providers.Contains(p)))
            .Select(p =>
            {
                var list = dayCalls.Where(c => c.Provider == p).ToList();
                var last = list.FirstOrDefault();
                var hasQuota = isToday && quotas.TryGetValue(p, out var q);
                return new ProviderToday(
                    p,
                    Sum(list),
                    ByHour(list),
                    ByHour(list.Where(c => c.Outcome != ApiUsageRecorder.Success)),
                    last?.AtUtc,
                    last?.Outcome,
                    hasQuota ? quotas[p].Used : null,
                    hasQuota ? quotas[p].Limit : null);
            })
            .ToList();

        var dayKeys = Enumerable.Range(0, days).Select(i => firstDay.AddDays(i)).ToList();
        var history = providers.Select(p =>
        {
            var byDay = calls.Where(c => c.Provider == p.Provider).ToLookup(c => DateOnly.FromDateTime(c.AtUtc + VnDay.Offset));
            return new ProviderHistory(p.Provider, dayKeys
                .Select(d => new ProviderDay(
                    d.ToString("yyyy-MM-dd"),
                    byDay[d].Count(),
                    byDay[d].Count(c => c.Outcome != ApiUsageRecorder.Success),
                    byDay[d].Sum(c => (long)c.TotalTokens)))
                .ToList());
        }).ToList();

        return Ok(new UsageDto(
            date.ToString("yyyy-MM-dd"),
            Sum(dayCalls),
            ByHour(dayCalls),
            providers,
            history,
            dayCalls.Take(100)
                .Select(c => new CallDto(c.AtUtc, c.Provider, c.Model, c.Endpoint, c.StatusCode, c.Outcome, c.DurationMs, c.ResponseBytes, c.TotalTokens, c.JobId))
                .ToList()));
    }

    /// <summary>Calls per hour of the Vietnam day (index 0 = 00:00-00:59).</summary>
    private static int[] ByHour(IEnumerable<ApiCall> calls)
    {
        var hours = new int[24];
        foreach (var c in calls)
        {
            hours[(c.AtUtc + VnDay.Offset).Hour]++;
        }

        return hours;
    }

    internal static ProviderTotals Sum(IReadOnlyCollection<ApiCall> list) => new(
        list.Count,
        list.Count(c => c.Outcome == ApiUsageRecorder.Success),
        list.Count(c => c.Outcome != ApiUsageRecorder.Success),
        list.Count(c => c.Outcome == ApiUsageRecorder.RateLimited),
        list.Count(c => c.Outcome == ApiUsageRecorder.Timeout),
        list.Count == 0 ? 0 : (int)list.Average(c => c.DurationMs),
        list.Count == 0 ? 0 : list.Max(c => c.DurationMs),
        list.Sum(c => c.ResponseBytes ?? 0),
        list.Sum(c => (long)c.TotalTokens),
        list.Sum(c => (long)c.PromptTokens),
        list.Sum(c => (long)c.OutputTokens));
}
