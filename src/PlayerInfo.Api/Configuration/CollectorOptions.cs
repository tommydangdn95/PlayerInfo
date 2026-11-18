namespace PlayerInfo.Api.Configuration;

public sealed class ApiKeysOptions
{
    public const string SectionName = "ApiKeys";
    public string ApiFootball { get; set; } = string.Empty;
    public string FootballData { get; set; } = string.Empty;
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
    /// </summary>
    public List<int> TrackedLeagueIds { get; set; } = [39, 140, 135, 78, 2];

    /// <summary>
    /// Current football season year (e.g., 2026).
    /// </summary>
    public int CurrentSeason { get; set; } = 2026;

    /// <summary>
    /// Trusted sports RSS feeds to ingest alongside Google News RSS.
    /// </summary>
    public List<RssFeedSource> SportsRssFeeds { get; set; } =
    [
        new("BBC Sport", "https://feeds.bbci.co.uk/sport/football/rss.xml", "en"),
        new("ESPN FC", "https://www.espn.com/espn/rss/soccer/news", "en"),
        new("VnExpress The Thao", "https://vnexpress.net/rss/the-thao.rss", "vi")
    ];

    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public int GeminiBatchSize { get; set; } = 15;
}

public sealed record RssFeedSource(string Name, string Url, string Language);
