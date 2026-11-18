using System.Text.Json;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Clients;

/// <summary>
/// Client for NewsData.io latest news API with daily credit tracking.
/// </summary>
public sealed class NewsDataClient(
    HttpClient http,
    QuotaManager quota,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<NewsDataClient> logger)
{
    private const string Provider = "NewsData";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<RawNewsItem>> SearchAsync(
        string query, string language = "en", CancellationToken ct = default)
    {
        var key = apiKeys.Value.NewsData;
        if (string.IsNullOrWhiteSpace(key))
        {
            logger.LogDebug("NewsData API key is not configured; skipping");
            return [];
        }

        if (!await quota.CanConsumeAsync(Provider, collectorOptions.Value.NewsDataDailyLimit, ct))
        {
            return [];
        }

        var url = $"latest?apikey={Uri.EscapeDataString(key)}&q={Uri.EscapeDataString(query)}&language={language}&category=sports";
        using var res = await http.GetAsync(url, ct);
        await quota.IncrementAsync(Provider, 1, ct);

        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("NewsData returned {StatusCode} for query {Query}", res.StatusCode, query);
            return [];
        }

        var payload = await res.Content.ReadFromJsonAsync<NewsDataResponse>(JsonOptions, ct);
        if (payload?.Results is null)
        {
            return [];
        }

        var items = new List<RawNewsItem>();
        foreach (var r in payload.Results)
        {
            if (string.IsNullOrWhiteSpace(r.Link) || string.IsNullOrWhiteSpace(r.Title))
            {
                continue;
            }

            var published = DateTime.TryParse(r.PubDate, out var dt)
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : DateTime.UtcNow;

            var snippet = r.Description?.Length > 400 ? r.Description[..400] : r.Description;
            items.Add(new RawNewsItem(r.Link, r.Title.Trim(), snippet, r.SourceId ?? "NewsData", language, published));
        }

        return items;
    }

    private sealed record NewsDataResponse(List<NewsDataArticle>? Results);
    private sealed record NewsDataArticle(string? Title, string? Link, string? Description, string? PubDate, string? SourceId);
}
