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

    public static readonly IReadOnlyList<int> DefaultTrackedLeagueIds = [39, 140, 135, 78, 2];

    public static readonly IReadOnlyList<RssFeedSource> DefaultSportsRssFeeds =
    [
        new("BBC Sport", "https://feeds.bbci.co.uk/sport/football/rss.xml", "en"),
        new("ESPN FC", "https://www.espn.com/espn/rss/soccer/news", "en"),
        new("VnExpress The Thao", "https://vnexpress.net/rss/the-thao.rss", "vi")
    ];

    /// <summary>
    /// Daily safety cap for API-Football (free tier is 100/day).
    /// </summary>
    public int ApiFootballDailyLimit { get; set; } = 90;

    /// <summary>
    /// Daily safety cap for NewsData.io (free tier is 200 credits/day).
    /// </summary>
    public int NewsDataDailyLimit { get; set; } = 180;

    /// <summary>
    /// League IDs in API-Football to sync injuries/fixtures (e.g., 39=EPL, 140=LaLiga, 2=UCL).
    /// Starts empty on purpose: the configuration binder appends to (rather than replaces) an
    /// initialized list, so defaults are applied in <see cref="ApplyDefaults"/> after binding.
    /// </summary>
    public List<int> TrackedLeagueIds { get; set; } = [];

    /// <summary>
    /// Current football season year (e.g., 2026).
    /// </summary>
    public int CurrentSeason { get; set; } = 2026;

    /// <summary>
    /// Trusted sports RSS feeds to ingest alongside Google News RSS. Same binding caveat as
    /// <see cref="TrackedLeagueIds"/>.
    /// </summary>
    public List<RssFeedSource> SportsRssFeeds { get; set; } = [];

    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public int GeminiBatchSize { get; set; } = 15;

    /// <summary>
    /// Fills empty lists with defaults and removes duplicate entries. Called once after configuration binding.
    /// </summary>
    public void ApplyDefaults()
    {
        TrackedLeagueIds = TrackedLeagueIds.Count == 0
            ? [.. DefaultTrackedLeagueIds]
            : TrackedLeagueIds.Distinct().ToList();

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
