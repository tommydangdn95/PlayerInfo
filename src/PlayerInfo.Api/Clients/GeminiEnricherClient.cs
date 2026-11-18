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
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("summaryVi")] string SummaryVi,
    [property: JsonPropertyName("matchedEntities")] List<string> MatchedEntities);

/// <summary>
/// Batches unprocessed news articles into a single Gemini API call returning structured JSON.
/// </summary>
public sealed class GeminiEnricherClient(
    HttpClient http,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<GeminiEnricherClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<GeminiArticleResult>> EnrichBatchAsync(
        IReadOnlyList<NewsArticle> batch,
        IReadOnlyList<string> trackedEntityNames,
        CancellationToken ct = default)
    {
        var key = apiKeys.Value.Gemini;
        if (string.IsNullOrWhiteSpace(key) || batch.Count == 0)
        {
            return [];
        }

        var prompt = BuildPrompt(batch, trackedEntityNames);
        var model = collectorOptions.Value.GeminiModel;
        var url = $"v1beta/models/{model}:generateContent?key={Uri.EscapeDataString(key)}";

        var payload = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                temperature = 0.2
            }
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var res = await http.PostAsync(url, content, ct);
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Gemini API returned {StatusCode}: {Body}", res.StatusCode, errBody);
            return [];
        }

        var responseDoc = await res.Content.ReadFromJsonAsync<GeminiGenerateResponse>(JsonOptions, ct);
        var jsonText = responseDoc?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<GeminiArticleResult>>(jsonText, JsonOptions);
            return parsed ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini JSON output: {Raw}", jsonText);
            return [];
        }
    }

    private static string BuildPrompt(IReadOnlyList<NewsArticle> batch, IReadOnlyList<string> trackedEntities)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a football news analyst. Analyze the following news articles and return ONLY a JSON array.");
        sb.AppendLine("Each object in the array must have:");
        sb.AppendLine("- articleId (number)");
        sb.AppendLine("- isRelevant (boolean: false if clickbait, betting spam, or unrelated to football)");
        sb.AppendLine("- category (one of: Injury, Transfer, MatchPerformance, OffPitch, Other)");
        sb.AppendLine("- summaryVi (concise 1-2 sentence summary in Vietnamese)");
        sb.AppendLine("- matchedEntities (array of team/player names mentioned, prioritizing tracked list below)");
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
