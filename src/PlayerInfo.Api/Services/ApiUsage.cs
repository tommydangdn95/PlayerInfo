using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Services;

/// <summary>
/// Stores every outbound API call (see <see cref="ApiCall"/>) and answers the counts Gemini's limits need.
/// Uses its own short-lived contexts so recording never touches the pipeline's pending changes.
/// </summary>
public sealed class ApiUsageRecorder(IDbContextFactory<AppDbContext> dbFactory, ILogger<ApiUsageRecorder> logger)
{
    /// <summary>Calls older than this are deleted.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(60);

    public const string GeminiProvider = "Gemini";

    public const string Success = "Success";
    public const string RateLimited = "RateLimited";
    public const string Overloaded = "Overloaded";
    public const string NotFound = "NotFound";
    public const string ClientError = "ClientError";
    public const string ServerError = "ServerError";
    public const string Timeout = "Timeout";
    public const string NetworkError = "NetworkError";

    public static string OutcomeFor(int statusCode) => statusCode switch
    {
        < 400 => Success,
        429 => RateLimited,
        503 => Overloaded,
        404 => NotFound,
        >= 500 => ServerError,
        _ => ClientError
    };

    public async Task RecordAsync(ApiCall call, CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ApiCalls.Add(call);
            await db.SaveChangesAsync(ct);

            // Now and then: keep the table small.
            if (call.Id % 200 == 0)
            {
                var cutoff = DateTime.UtcNow - Retention;
                db.ApiCalls.RemoveRange(db.ApiCalls.Where(c => c.AtUtc < cutoff));
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Usage tracking must never break the call it describes.
            logger.LogWarning(ex, "Could not record a {Provider} API call", call.Provider);
        }
    }

    /// <summary>Gemini requests to <paramref name="model"/> since <paramref name="sinceUtc"/>, failed ones included (Google counts them).</summary>
    public async Task<int> CountGeminiSinceAsync(string model, DateTime sinceUtc, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.ApiCalls.AsNoTracking()
            .CountAsync(c => c.Provider == GeminiProvider && c.Model == model && c.AtUtc >= sinceUtc, ct);
    }
}

/// <summary>Google's Gemini quota day: midnight to midnight Pacific time.</summary>
public static class GeminiQuotaDay
{
    public static readonly TimeZoneInfo Pacific = Find("America/Los_Angeles") ?? Find("Pacific Standard Time")
        ?? TimeZoneInfo.CreateCustomTimeZone("UTC-08", TimeSpan.FromHours(-8), "UTC-08", "UTC-08");

    public static DateOnly Of(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Pacific));

    public static DateOnly Today() => Of(DateTime.UtcNow);

    public static DateTime StartUtc(DateOnly day)
        => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Pacific);

    /// <summary>When today's quota resets.</summary>
    public static DateTime NextResetUtc() => StartUtc(Today().AddDays(1));

    private static TimeZoneInfo? Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
