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

    /// <summary>
    /// Main model: the best one available on the free tier, Gemini 4 Hydrogen (20 requests/day, 5/minute).
    /// When it is out of quota or overloaded, the fallbacks take over: the Flash models, then the Flash Lite
    /// models that carry the volume (500/day each), then older models as a last resort.
    /// </summary>
    public string GeminiModel { get; set; } = "gemini-4-hydrogen";

    /// <summary>
    /// Tried in order when a model is overloaded (503), retired (404), rate limited (429), times out, or has used
    /// its daily/minute limit. Same binding caveat as <see cref="Leagues"/>.
    /// </summary>
    public List<string> GeminiFallbackModels { get; set; } = [];

    public static readonly IReadOnlyList<string> DefaultGeminiFallbackModels =
    [
        "gemini-3.8-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash-lite",
        "gemini-3.1-flash-lite",
        "gemini-3-flash-preview",
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite"
    ];

    /// <summary>
    /// Free-tier limits per model (Google AI Studio, "Rate limits by model"). Failed requests count too.
    /// Same binding caveat as <see cref="Leagues"/>.
    /// </summary>
    public List<GeminiModelLimit> GeminiModelLimits { get; set; } = [];

    public static readonly IReadOnlyList<GeminiModelLimit> DefaultGeminiModelLimits =
    [
        new("gemini-4-hydrogen", 20, 5, 250_000),
        new("gemini-3.8-flash", 20, 5, 250_000),
        new("gemini-3.6-flash", 20, 5, 250_000),
        new("gemini-3.5-flash-lite", 500, 15, 250_000),
        new("gemini-3.1-flash-lite", 500, 15, 250_000),
        new("gemini-3-flash-preview", 20, 5, 250_000),
        new("gemini-2.5-flash", 20, 5, 250_000),
        new("gemini-2.5-flash-lite", 20, 10, 250_000)
    ];

    /// <summary>Requests per day for a model not listed in <see cref="GeminiModelLimits"/>; 0 = no limit.</summary>
    public int GeminiDailyRequestLimit { get; set; } = 20;

    /// <summary>The main model, then the fallbacks, without duplicates.</summary>
    public IEnumerable<string> GeminiModels()
        => new[] { GeminiModel }.Concat(GeminiFallbackModels)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Limits of <paramref name="model"/>; unlisted models get <see cref="GeminiDailyRequestLimit"/> per day.</summary>
    public GeminiModelLimit LimitFor(string model)
        => GeminiModelLimits.FirstOrDefault(l => string.Equals(l.Model, model, StringComparison.OrdinalIgnoreCase))
           ?? new GeminiModelLimit(model, GeminiDailyRequestLimit);

    public int GeminiBatchSize { get; set; } = 15;

    /// <summary>
    /// News older than this many days is deleted (with its team links and emptied stories) at the end of every
    /// news run, keeping the database well under the Neon free tier. 0 = keep forever.
    /// </summary>
    public int ArticleRetentionDays { get; set; } = 60;


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

        if (GeminiFallbackModels.Count == 0)
        {
            GeminiFallbackModels = [.. DefaultGeminiFallbackModels];
        }

        GeminiModelLimits = GeminiModelLimits.Count == 0
            ? [.. DefaultGeminiModelLimits]
            : GeminiModelLimits
                .Where(l => !string.IsNullOrWhiteSpace(l.Model))
                .Select(l => l with { Model = l.Model.Trim() })
                .DistinctBy(l => l.Model, StringComparer.OrdinalIgnoreCase)
                .ToList();

        GeminiBatchSize = Math.Max(1, GeminiBatchSize);
    }
}

public sealed record RssFeedSource(string Name, string Url, string Language);

/// <summary>Free-tier limits of a Gemini model; 0 = no limit.</summary>
public sealed record GeminiModelLimit(string Model, int RequestsPerDay, int RequestsPerMinute = 0, int TokensPerMinute = 0);

/// <summary>An ESPN soccer league, e.g. ("eng.1", "Premier League").</summary>
public sealed record LeagueOption(string Slug, string Name);
