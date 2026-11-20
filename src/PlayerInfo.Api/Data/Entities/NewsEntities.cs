namespace PlayerInfo.Api.Data.Entities;

public sealed class NewsArticle
{
    public long Id { get; set; }
    public string UrlHash { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Snippet { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public string Language { get; set; } = "en";
    public string? SummaryVi { get; set; }

    /// <summary>Topic, one of <see cref="Services.NewsTaxonomy.Topics"/> (Transfer, Injury, Match, ...).</summary>
    public string Category { get; set; } = "Other";

    public bool IsRelevant { get; set; } = true;

    /// <summary>Importance 4-5 as judged by Gemini. Always false without AI.</summary>
    public bool IsHot { get; set; }

    /// <summary>1 (minor) .. 5 (must-know), from Gemini; 0 when not AI-processed.</summary>
    public int Importance { get; set; }

    /// <summary>What kind of claim the article makes: Official, Report, Rumour or Opinion (Gemini).</summary>
    public string? ClaimType { get; set; }

    /// <summary>Credibility of the source: 1 (best) .. 3, 0 = junk (hidden). See <see cref="NewsSource"/>.</summary>
    public int SourceTier { get; set; } = 3;

    /// <summary>The story (same news reported by several sources) this article belongs to.</summary>
    public long? StoryId { get; set; }
    public Story? Story { get; set; }

    public bool IsAiProcessed { get; set; }
    public bool IsNotified { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ArticleEntity> Entities { get; set; } = [];
}

public sealed class ArticleEntity
{
    public long ArticleId { get; set; }
    public NewsArticle Article { get; set; } = null!;
    public string EntityType { get; set; } = "Team"; // "Team" or "Player"
    public int EntityId { get; set; }
}

/// <summary>
/// One piece of news (e.g. "Arsenal bid for X") reported by one or more articles. Gemini groups articles
/// into stories and writes the Vietnamese headline/summary; confidence and source counts are computed.
/// </summary>
public sealed class Story
{
    public long Id { get; set; }
    public string HeadlineVi { get; set; } = string.Empty;
    public string? SummaryVi { get; set; }
    public string Topic { get; set; } = "Other";

    /// <summary>1 (minor) .. 5 (must-know).</summary>
    public int Importance { get; set; } = 1;

    /// <summary>Official, Confirmed (several reliable sources), Rumour or Opinion.</summary>
    public string Confidence { get; set; } = "Rumour";

    /// <summary>Distinct sources that reported it.</summary>
    public int SourceCount { get; set; }

    /// <summary>Best (lowest) tier among its sources.</summary>
    public int BestTier { get; set; } = 3;

    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When it was last pushed to Telegram; it is pushed again only after an update.</summary>
    public DateTime? NotifiedAtUtc { get; set; }

    public ICollection<NewsArticle> Articles { get; set; } = [];
}

/// <summary>
/// Source credibility rule: a source whose name or URL host contains <see cref="Pattern"/> gets <see cref="Tier"/>.
/// The longest matching pattern wins; unknown sources are tier 3.
/// </summary>
public sealed class NewsSource
{
    public int Id { get; set; }
    public string Pattern { get; set; } = string.Empty;

    /// <summary>1 = most reliable, 2 = reliable, 3 = tabloid/aggregator, 0 = junk (betting, clickbait: hidden).</summary>
    public int Tier { get; set; } = 3;
}
