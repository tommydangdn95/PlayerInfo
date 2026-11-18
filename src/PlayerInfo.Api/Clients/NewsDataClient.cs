using System.Globalization;
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
    private const string Provider = QuotaManager.NewsDataProvider;

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

        // Counted before sending; see ApiFootballClient.SendAsync.
        await quota.IncrementAsync(Provider, 1, ct);

        NewsDataResponse? payload;
        try
        {
            var url = $"latest?apikey={Uri.EscapeDataString(key)}&q={Uri.EscapeDataString(query)}&language={language}&category=sports";
            using var res = await http.GetAsync(url, ct);

            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("NewsData returned {StatusCode} for query {Query}", res.StatusCode, query);
                return [];
            }

            payload = await res.Content.ReadFromJsonAsync<NewsDataResponse>(JsonOptions, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "NewsData request failed for query {Query}", query);
            return [];
        }

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

            // NewsData pubDate is "yyyy-MM-dd HH:mm:ss" in UTC.
            var published = DateTime.TryParse(
                r.PubDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt)
                ? dt
                : DateTime.UtcNow;

            var snippet = r.Description?.Length > 400 ? r.Description[..400] : r.Description;
            items.Add(new RawNewsItem(r.Link, r.Title.Trim(), snippet, r.SourceId ?? "NewsData", language, published));
        }

        return items;
    }

    /// <summary>
    /// Builds a NewsData "q" value from keywords: multi-word keywords are quoted, and the
    /// result stays within the free plan's 100-character query limit.
    /// </summary>
    public static string BuildQuery(IEnumerable<string> keywords, int maxLength = 100)
    {
        var parts = new List<string>();
        var length = 0;
        foreach (var raw in keywords)
        {
            var kw = raw.Trim().Replace("\"", string.Empty);
            if (kw.Length == 0)
            {
                continue;
            }

            var term = kw.Contains(' ') ? $"\"{kw}\"" : kw;
            var added = parts.Count == 0 ? term.Length : term.Length + 4; // " OR "
            if (length + added > maxLength)
            {
                break;
            }

            parts.Add(term);
            length += added;
        }

        return string.Join(" OR ", parts);
    }

    private sealed record NewsDataResponse(List<NewsDataArticle>? Results);
    private sealed record NewsDataArticle(string? Title, string? Link, string? Description, string? PubDate, string? SourceId);
}
