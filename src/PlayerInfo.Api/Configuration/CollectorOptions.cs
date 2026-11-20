namespace PlayerInfo.Api.Configuration;

public sealed class ApiKeysOptions
{
    public const string SectionName = "ApiKeys";
    public string ApiFootball { get; set; } = string.Empty;
    public string NewsData { get; set; } = string.Empty;
    public string Gemini { get; set; } = string.Empty;
}

public sealed class CronOptions
{
    public const string SectionName = "Cron";
    public string SecretKey { get; set; } = string.Empty;
}

public sealed class TelegramOptions
{
    public const string SectionName = "Telegram";
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    /// <summary>ESPN league slugs (same set as the FootballNews project).</summary>
    public static readonly IReadOnlyList<LeagueOption> DefaultLeagues =
    [
        new("eng.1", "Premier League"),
        new("esp.1", "La Liga"),
        new("ita.1", "Serie A"),
        new("ger.1", "Bundesliga"),
        new("fra.1", "Ligue 1"),
        new("uefa.champions", "Champions League"),
        new("uefa.europa", "Europa League"),
        new("uefa.europa.conf", "Conference League")
    ];

    public static readonly IReadOnlyList<RssFeedSource> DefaultSportsRssFeeds =
    [
        new("BBC Sport", "https://feeds.bbci.co.uk/sport/football/rss.xml", "en"),
        new("ESPN FC", "https://www.espn.com/espn/rss/soccer/news", "en"),
        new("VnExpress The Thao", "https://vnexpress.net/rss/the-thao.rss", "vi")
    ];

    /// <summary>
    /// Daily safety cap for API-Football (free tier is 100/day). Only transfers use API-Football.
    /// </summary>
    public int ApiFootballDailyLimit { get; set; } = 90;

    /// <summary>
    /// Daily safety cap for NewsData.io (free tier is 200 credits/day).
    /// </summary>
    public int NewsDataDailyLimit { get; set; } = 180;

    /// <summary>
    /// ESPN leagues whose fixtures are synced and whose teams can be picked in the admin UI.
    /// Starts empty on purpose: the configuration binder appends to (rather than replaces) an
    /// initialized list, so defaults are applied in <see cref="ApplyDefaults"/> after binding.
    /// </summary>
    public List<LeagueOption> Leagues { get; set; } = [];

    /// <summary>
    /// Trusted sports RSS feeds to ingest alongside Google News RSS. Same binding caveat as
    /// <see cref="Leagues"/>.
    /// </summary>
    public List<RssFeedSource> SportsRssFeeds { get; set; } = [];

    public string GeminiModel { get; set; } = "gemini-3.8-flash";
    public int GeminiBatchSize { get; set; } = 15;

    /// <summary>
    /// Fills empty lists with defaults and removes duplicate entries. Called once after configuration binding.
    /// </summary>
    public void ApplyDefaults()
    {
        Leagues = Leagues.Count == 0
            ? [.. DefaultLeagues]
            : Leagues
                .Where(l => !string.IsNullOrWhiteSpace(l.Slug))
                .Select(l => l with { Slug = l.Slug.Trim().ToLowerInvariant(), Name = string.IsNullOrWhiteSpace(l.Name) ? l.Slug.Trim() : l.Name.Trim() })
                .DistinctBy(l => l.Slug)
                .ToList();

        SportsRssFeeds = SportsRssFeeds.Count == 0
            ? [.. DefaultSportsRssFeeds]
            : SportsRssFeeds
                .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                .DistinctBy(f => f.Url, StringComparer.OrdinalIgnoreCase)
                .ToList();

        GeminiBatchSize = Math.Max(1, GeminiBatchSize);
    }
}

public sealed record RssFeedSource(string Name, string Url, string Language);

/// <summary>An ESPN soccer league, e.g. ("eng.1", "Premier League").</summary>
public sealed record LeagueOption(string Slug, string Name);
