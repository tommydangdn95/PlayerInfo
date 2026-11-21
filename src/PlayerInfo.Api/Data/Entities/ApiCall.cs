namespace PlayerInfo.Api.Data.Entities;

/// <summary>
/// One outbound HTTP request of any provider (ESPN, RSS, API-Football, NewsData, Gemini, Telegram), every attempt,
/// successful or not. Feeds the API usage dashboard and Gemini's per-model daily/minute limits. Kept 20 days.
/// </summary>
public sealed class ApiCall
{
    public long Id { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Provider name as used in the live log: ESPN, RSS, API-Football, NewsData, Gemini, Telegram.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gemini model, when the provider is Gemini.</summary>
    public string? Model { get; set; }

    /// <summary>Host and path, without query string (no secrets).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>HTTP status; null when the request never got an answer (timeout, network error).</summary>
    public int? StatusCode { get; set; }

    /// <summary>Success, RateLimited (429), Overloaded (503), NotFound (404), ClientError, ServerError, Timeout, NetworkError.</summary>
    public string Outcome { get; set; } = "Success";

    public int DurationMs { get; set; }
    public long? ResponseBytes { get; set; }

    /// <summary>Gemini token counts from usageMetadata (0 otherwise).</summary>
    public int PromptTokens { get; set; }
    public int OutputTokens { get; set; }
    public int ThoughtTokens { get; set; }
    public int TotalTokens { get; set; }

    /// <summary>Articles sent in a Gemini request.</summary>
    public int ArticleCount { get; set; }

    /// <summary>Collector job that made the call, if any.</summary>
    public string? JobId { get; set; }
}
