using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Clients;

public sealed record GeminiArticleResult(
    [property: JsonPropertyName("articleId")] long ArticleId,
    [property: JsonPropertyName("isRelevant")] bool IsRelevant,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("summaryVi")] string? SummaryVi,
    [property: JsonPropertyName("matchedEntities")] List<string>? MatchedEntities,
    [property: JsonPropertyName("isHot")] bool IsHot = false);

/// <summary>
/// Outcome of one Gemini batch. <see cref="Succeeded"/> is false when the call failed
/// (HTTP error, timeout, unparsable output), so the caller can retry those articles later.
/// </summary>
public sealed record GeminiBatchResult(bool Succeeded, IReadOnlyList<GeminiArticleResult> Articles)
{
    public static readonly GeminiBatchResult Failed = new(false, []);
}

/// <summary>
/// Batches unprocessed news articles into a single Gemini API call returning structured JSON.
/// </summary>
public sealed class GeminiEnricherClient(
    HttpClient http,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<GeminiEnricherClient> logger)
{
    public static readonly IReadOnlyList<string> Categories = ["Injury", "Transfer", "MatchPerformance", "OffPitch", "Other"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Structured-output schema so Gemini returns exactly a JSON array of article results.
    /// </summary>
    private static readonly object ResponseSchema = new
    {
        type = "ARRAY",
        items = new
        {
            type = "OBJECT",
            properties = new Dictionary<string, object>
            {
                ["articleId"] = new { type = "INTEGER" },
                ["isRelevant"] = new { type = "BOOLEAN" },
                ["category"] = new { type = "STRING", @enum = Categories },
                ["summaryVi"] = new { type = "STRING" },
                ["matchedEntities"] = new { type = "ARRAY", items = new { type = "STRING" } },
                ["isHot"] = new { type = "BOOLEAN" }
            },
            required = new[] { "articleId", "isRelevant", "category", "summaryVi", "matchedEntities", "isHot" }
        }
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKeys.Value.Gemini);

    public async Task<GeminiBatchResult> EnrichBatchAsync(
        IReadOnlyList<NewsArticle> batch,
        IReadOnlyList<string> trackedEntityNames,
        CancellationToken ct = default)
    {
        if (!IsConfigured || batch.Count == 0)
        {
            return GeminiBatchResult.Failed;
        }

        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = BuildPrompt(batch, trackedEntityNames) } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = ResponseSchema,
                temperature = 0.2
            }
        };

        string? jsonText;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{collectorOptions.Value.GeminiModel}:generateContent")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            // Key in a header rather than the query string so it never ends up in URLs/logs.
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKeys.Value.Gemini);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var errBody = await res.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Gemini API returned {StatusCode}: {Body}", res.StatusCode, errBody);
                return GeminiBatchResult.Failed;
            }

            var responseDoc = await res.Content.ReadFromJsonAsync<GeminiGenerateResponse>(JsonOptions, ct);
            jsonText = responseDoc?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gemini request failed for a batch of {Count} articles", batch.Count);
            return GeminiBatchResult.Failed;
        }

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            logger.LogWarning("Gemini returned an empty response for a batch of {Count} articles", batch.Count);
            return GeminiBatchResult.Failed;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<GeminiArticleResult>>(jsonText, JsonOptions);
            return new GeminiBatchResult(true, parsed ?? []);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini JSON output: {Raw}", jsonText);
            return GeminiBatchResult.Failed;
        }
    }

    private static string BuildPrompt(IReadOnlyList<NewsArticle> batch, IReadOnlyList<string> trackedEntities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a football news analyst. Analyze the following news articles and return ONLY a JSON array with one object per article.");
        sb.AppendLine("Each object in the array must have:");
        sb.AppendLine("- articleId (number, copied from ID)");
        sb.AppendLine("- isRelevant (boolean: false if clickbait, betting spam, or unrelated to football)");
        sb.AppendLine($"- category (one of: {string.Join(", ", Categories)})");
        sb.AppendLine("- summaryVi (concise 1-2 sentence summary in Vietnamese)");
        sb.AppendLine("- matchedEntities (array of tracked team/player names from the list below that the article is about, using the exact spelling from the list)");
        sb.AppendLine("- isHot (boolean: true ONLY for important, newsworthy news about a tracked team/player that a fan must not miss:");
        sb.AppendLine("  a confirmed or advanced transfer, a significant injury or return of a key player, a manager sacked/appointed,");
        sb.AppendLine("  a final match result or major upset, a suspension/disciplinary decision, an official club announcement.");
        sb.AppendLine("  false for previews, predictions, opinion, gossip round-ups, betting tips, fan reactions, minor or old news.");
        sb.AppendLine("  At most about 1 in 5 articles should be hot.)");
        sb.AppendLine();
        sb.AppendLine($"Tracked entities: {string.Join(", ", trackedEntities)}");
        sb.AppendLine();
        sb.AppendLine("Articles:");
        foreach (var a in batch)
        {
            sb.AppendLine($"- ID={a.Id} | Source={a.SourceName} | Title={a.Title} | Snippet={a.Snippet}");
        }

        return sb.ToString();
    }

    private sealed record GeminiGenerateResponse(List<GeminiCandidate>? Candidates);
    private sealed record GeminiCandidate(GeminiContent? Content);
    private sealed record GeminiContent(List<GeminiPart>? Parts);
    private sealed record GeminiPart(string? Text);
}
