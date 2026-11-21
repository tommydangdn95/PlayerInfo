using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Data.Entities;
using PlayerInfo.Api.Logging;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Clients;

public sealed record GeminiArticleResult(
    [property: JsonPropertyName("articleId")] long ArticleId,
    [property: JsonPropertyName("isRelevant")] bool IsRelevant,
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("claimType")] string? ClaimType,
    [property: JsonPropertyName("importance")] int Importance,
    [property: JsonPropertyName("summaryVi")] string? SummaryVi,
    [property: JsonPropertyName("matchedEntities")] List<string>? MatchedEntities,
    [property: JsonPropertyName("storyRef")] string? StoryRef);

/// <summary>Latest state of a story (new label "N1".., or existing "S{id}") after this batch.</summary>
public sealed record GeminiStoryResult(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("headlineVi")] string? HeadlineVi,
    [property: JsonPropertyName("summaryVi")] string? SummaryVi,
    [property: JsonPropertyName("topic")] string? Topic,
    [property: JsonPropertyName("importance")] int Importance);

/// <summary>A recent story offered to Gemini so new articles about the same news join it.</summary>
public sealed record OpenStory(long Id, string Teams, string Topic, string HeadlineVi, string? SummaryVi)
{
    public string Ref => $"S{Id}";
}

/// <summary>
/// Outcome of one Gemini batch. <see cref="Succeeded"/> is false when the call failed
/// (HTTP error, timeout, unparsable output), so the caller can retry those articles later.
/// </summary>
public sealed record GeminiBatchResult(bool Succeeded, IReadOnlyList<GeminiArticleResult> Articles, IReadOnlyList<GeminiStoryResult> Stories)
{
    public static readonly GeminiBatchResult Failed = new(false, [], []);
}

/// <summary>
/// Batches unprocessed news articles into a single Gemini API call returning structured JSON.
/// </summary>
public sealed class GeminiEnricherClient(
    HttpClient http,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ApiUsageRecorder usage,
    ILogger<GeminiEnricherClient> logger)
{
    /// <summary>Models that answered 429 or reached their daily limit during this client's lifetime (one pipeline run).</summary>
    private readonly HashSet<string> _exhausted = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Structured-output schema: one result per article plus the stories they belong to.
    /// </summary>
    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new Dictionary<string, object>
        {
            ["articles"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["articleId"] = new { type = "INTEGER" },
                        ["isRelevant"] = new { type = "BOOLEAN" },
                        ["topic"] = new { type = "STRING", @enum = NewsTaxonomy.Topics },
                        ["claimType"] = new { type = "STRING", @enum = NewsTaxonomy.ClaimTypes },
                        ["importance"] = new { type = "INTEGER" },
                        ["summaryVi"] = new { type = "STRING" },
                        ["matchedEntities"] = new { type = "ARRAY", items = new { type = "STRING" } },
                        ["storyRef"] = new { type = "STRING" }
                    },
                    required = new[] { "articleId", "isRelevant", "topic", "claimType", "importance", "summaryVi", "matchedEntities", "storyRef" }
                }
            },
            ["stories"] = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new Dictionary<string, object>
                    {
                        ["ref"] = new { type = "STRING" },
                        ["headlineVi"] = new { type = "STRING" },
                        ["summaryVi"] = new { type = "STRING" },
                        ["topic"] = new { type = "STRING", @enum = NewsTaxonomy.Topics },
                        ["importance"] = new { type = "INTEGER" }
                    },
                    required = new[] { "ref", "headlineVi", "summaryVi", "topic", "importance" }
                }
            }
        },
        required = new[] { "articles", "stories" }
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKeys.Value.Gemini);

    public async Task<GeminiBatchResult> EnrichBatchAsync(
        IReadOnlyList<NewsArticle> batch,
        IReadOnlyList<string> trackedEntityNames,
        IReadOnlyList<OpenStory> openStories,
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
                new { parts = new[] { new { text = BuildPrompt(batch, trackedEntityNames, openStories) } } }
            },
            generationConfig = new
            {
                responseMimeType = "application/json",
                responseSchema = ResponseSchema,
                temperature = 0.2
            }
        };

        var body = JsonSerializer.Serialize(payload);
        string? jsonText = null;
        var answered = false;
        foreach (var model in collectorOptions.Value.GeminiModels())
        {
            // Every attempt costs a request from that model's own quota: skip models that used it up.
            if (_exhausted.Contains(model) || await LimitReachedAsync(model, ct))
            {
                continue;
            }

            var (ok, retryElsewhere, text) = await SendAsync(model, body, batch.Count, ct);
            if (ok)
            {
                jsonText = text;
                answered = true;
                break;
            }

            if (!retryElsewhere)
            {
                break;
            }
        }

        if (!answered)
        {
            return GeminiBatchResult.Failed;
        }

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            logger.LogWarning("Gemini returned an empty response for a batch of {Count} articles", batch.Count);
            return GeminiBatchResult.Failed;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<GeminiOutput>(jsonText, JsonOptions);
            if (parsed?.Articles is null)
            {
                logger.LogWarning("Gemini JSON output has no articles: {Raw}", jsonText);
                return GeminiBatchResult.Failed;
            }

            return new GeminiBatchResult(true, parsed.Articles, parsed.Stories ?? []);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse Gemini JSON output: {Raw}", jsonText);
            return GeminiBatchResult.Failed;
        }
    }

    /// <summary>
    /// Calls one model (the HTTP trace handler records the attempt, its model and tokens). Overload (503),
    /// retired models (404), other 5xx and timeouts are worth trying on the next model. A rate limit (429) uses up
    /// only that model's quota (each model has its own), so the model is skipped for the rest of this run and the
    /// next one is tried. Bad requests and auth errors stop: every model would fail the same way.
    /// </summary>
    private async Task<(bool Ok, bool RetryElsewhere, string? Text)> SendAsync(string model, string body, int batchCount, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{model}:generateContent")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            // Key in a header rather than the query string so it never ends up in URLs/logs.
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKeys.Value.Gemini);
            req.Options.Set(HttpTraceHandler.ArticleCountOption, batchCount);

            using var res = await http.SendAsync(req, ct);
            var status = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode)
            {
                var errBody = await res.Content.ReadAsStringAsync(ct);
                if (status == 429)
                {
                    _exhausted.Add(model);
                }

                var retry = status is 404 or 429 or >= 500;
                logger.LogWarning("Gemini model {Model} returned {StatusCode}{Next}: {Body}",
                    model, res.StatusCode,
                    status == 429 ? " (its quota is used up; skipping it for this run)" : retry ? ", trying the next model" : string.Empty,
                    errBody);
                return (false, retry, null);
            }

            var responseDoc = await res.Content.ReadFromJsonAsync<GeminiGenerateResponse>(JsonOptions, ct);
            return (true, false, responseDoc?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gemini model {Model} request failed for a batch of {Count} articles", model, batchCount);
            return (false, true, null);
        }
    }

    /// <summary>True when the model used its requests per day (Google quota day) or per minute, failed requests included.</summary>
    private async Task<bool> LimitReachedAsync(string model, CancellationToken ct)
    {
        var limit = collectorOptions.Value.LimitFor(model);

        if (limit.RequestsPerDay > 0)
        {
            var today = await usage.CountGeminiSinceAsync(model, GeminiQuotaDay.StartUtc(GeminiQuotaDay.Today()), ct);
            if (today >= limit.RequestsPerDay)
            {
                logger.LogWarning("Gemini model {Model} used its daily limit ({Used}/{Limit}); skipping it until the quota day resets",
                    model, today, limit.RequestsPerDay);
                _exhausted.Add(model);
                return true;
            }
        }

        if (limit.RequestsPerMinute > 0)
        {
            var lastMinute = await usage.CountGeminiSinceAsync(model, DateTime.UtcNow.AddMinutes(-1), ct);
            if (lastMinute >= limit.RequestsPerMinute)
            {
                logger.LogWarning("Gemini model {Model} used its per-minute limit ({Used}/{Limit}); trying the next model",
                    model, lastMinute, limit.RequestsPerMinute);
                return true;
            }
        }

        return false;
    }

    internal static string BuildPrompt(IReadOnlyList<NewsArticle> batch, IReadOnlyList<string> trackedEntities, IReadOnlyList<OpenStory> openStories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the editor of a Vietnamese football news service. Analyze the articles below and return ONLY JSON: {\"articles\": [...], \"stories\": [...]}.");
        sb.AppendLine();
        sb.AppendLine("For EVERY article, one object in \"articles\":");
        sb.AppendLine("- articleId: copied from ID");
        sb.AppendLine("- isRelevant: false for betting/odds/tips, fantasy-football advice, quizzes, clickbait, or news not about football or not about the tracked teams/players");
        sb.AppendLine("- topic, one of:");
        sb.AppendLine("  Transfer (signings, sales, loans, contracts), Injury (injuries, returns, fitness, squad availability),");
        sb.AppendLine("  Match (results, match reports, performances), PreMatch (previews, predicted line-ups, pre-match press conferences),");
        sb.AppendLine("  Manager (coach/staff: appointments, sackings, statements), Turmoil (internal problems, rifts, unrest, crisis, bad run),");
        sb.AppendLine("  Discipline (cards, bans, charges, investigations), Club (ownership, finances, stadium, business), Other");
        sb.AppendLine("- claimType: Official (announced/confirmed by the club, league or player; final match results), Report (stated as fact by the journalist),");
        sb.AppendLine("  Rumour (speculation: 'linked with', 'eyeing', 'could', gossip columns), Opinion (columns, analysis, predictions, fan views)");
        sb.AppendLine("- importance 1-5 for fans of the tracked team: 5 must-know (completed transfer, manager sacked/appointed, long injury to a key player, big result),");
        sb.AppendLine("  4 significant, 3 notable, 2 minor, 1 filler/trivia");
        sb.AppendLine("- summaryVi: 1-2 sentence Vietnamese summary of the article");
        sb.AppendLine("- matchedEntities: tracked team/player names (exact spelling from the list) the article is about");
        sb.AppendLine("- storyRef: the story it belongs to. A story is ONE piece of news (same event, same people) reported by several articles.");
        sb.AppendLine("  Use an open story id (e.g. \"S12\") if the article reports the same news; otherwise a new label \"N1\", \"N2\"...");
        sb.AppendLine("  shared by all articles of this batch about the same news. Use \"\" for irrelevant articles.");
        sb.AppendLine();
        sb.AppendLine("For every story used by a relevant article (new labels and open ids that got new articles), one object in \"stories\":");
        sb.AppendLine("- ref: the label or id");
        sb.AppendLine("- headlineVi: short factual Vietnamese headline (max 15 words, no clickbait)");
        sb.AppendLine("- summaryVi: 2-3 Vietnamese sentences combining everything known so far (the open story's summary plus new details);");
        sb.AppendLine("  say clearly if it is still only a rumour");
        sb.AppendLine("- topic and importance for the story as a whole");
        sb.AppendLine();
        sb.AppendLine($"Tracked entities: {string.Join(", ", trackedEntities)}");
        sb.AppendLine();
        sb.AppendLine("Open stories (last 48 hours):");
        if (openStories.Count == 0)
        {
            sb.AppendLine("- (none)");
        }

        foreach (var s in openStories)
        {
            sb.AppendLine($"- {s.Ref} | {s.Teams} | {s.Topic} | {s.HeadlineVi} | {s.SummaryVi}");
        }

        sb.AppendLine();
        sb.AppendLine("Articles:");
        foreach (var a in batch)
        {
            sb.AppendLine($"- ID={a.Id} | Source={a.SourceName} | Title={a.Title} | Snippet={a.Snippet}");
        }

        return sb.ToString();
    }

    private sealed record GeminiOutput(List<GeminiArticleResult>? Articles, List<GeminiStoryResult>? Stories);
    private sealed record GeminiGenerateResponse(List<GeminiCandidate>? Candidates);
    private sealed record GeminiCandidate(GeminiContent? Content);
    private sealed record GeminiContent(List<GeminiPart>? Parts);
    private sealed record GeminiPart(string? Text);
}
