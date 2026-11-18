using System.ServiceModel.Syndication;
using System.Xml;
using HtmlAgilityPack;
using PlayerInfo.Api.Configuration;

namespace PlayerInfo.Api.Clients;

public sealed record RawNewsItem(
    string Url,
    string Title,
    string? Snippet,
    string SourceName,
    string Language,
    DateTime PublishedAtUtc);

/// <summary>
/// Fetches and parses RSS feeds from Google News and trusted sports outlets.
/// </summary>
public sealed class RssNewsClient(HttpClient http, ILogger<RssNewsClient> logger)
{
    public async Task<IReadOnlyList<RawNewsItem>> FetchGoogleNewsAsync(
        string query, string language = "en", CancellationToken ct = default)
    {
        var isVi = string.Equals(language, "vi", StringComparison.OrdinalIgnoreCase);
        var hl = isVi ? "vi" : "en-US";
        var gl = isVi ? "VN" : "US";
        var ceid = isVi ? "VN:vi" : "US:en";
        var encodedQuery = Uri.EscapeDataString($"{query} when:1d");
        var url = $"https://news.google.com/rss/search?q={encodedQuery}&hl={hl}&gl={gl}&ceid={ceid}";

        return await FetchFeedAsync(url, "Google News", language, ct);
    }

    public Task<IReadOnlyList<RawNewsItem>> FetchSportsFeedAsync(
        RssFeedSource source, CancellationToken ct = default)
        => FetchFeedAsync(source.Url, source.Name, source.Language, ct);

    private async Task<IReadOnlyList<RawNewsItem>> FetchFeedAsync(
        string feedUrl, string defaultSource, string language, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "PlayerInfoBot/1.0 (+https://github.com)");

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("RSS feed {Source} ({Url}) returned {StatusCode}", defaultSource, feedUrl, res.StatusCode);
                return [];
            }

            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var xmlReader = XmlReader.Create(stream, new XmlReaderSettings { Async = true });
            var feed = SyndicationFeed.Load(xmlReader);
            if (feed is null)
            {
                return [];
            }

            var items = new List<RawNewsItem>();
            foreach (var entry in feed.Items)
            {
                var link = entry.Links.FirstOrDefault()?.Uri.ToString();
                var title = entry.Title?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(link) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                var rawSnippet = entry.Summary?.Text;
                var cleanSnippet = StripHtml(rawSnippet);
                if (cleanSnippet.Length > 400)
                {
                    cleanSnippet = cleanSnippet[..400];
                }

                var sourceName = entry.SourceFeed?.Title?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(sourceName))
                {
                    sourceName = defaultSource;
                }

                var published = entry.PublishDate != DateTimeOffset.MinValue
                    ? entry.PublishDate.UtcDateTime
                    : DateTime.UtcNow;

                items.Add(new RawNewsItem(link, title, cleanSnippet, sourceName, language, published));
            }

            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to parse RSS feed {Source} ({Url})", defaultSource, feedUrl);
            return [];
        }
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var text = HtmlEntity.DeEntitize(doc.DocumentNode.InnerText);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
