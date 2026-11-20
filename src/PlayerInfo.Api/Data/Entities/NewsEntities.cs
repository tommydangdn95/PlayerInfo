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
    public string Category { get; set; } = "Other"; // Injury, Transfer, MatchPerformance, OffPitch, Other
    public bool IsRelevant { get; set; } = true;

    /// <summary>Breaking/major news for a tracked club, as judged by Gemini. Always false without AI.</summary>
    public bool IsHot { get; set; }
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
