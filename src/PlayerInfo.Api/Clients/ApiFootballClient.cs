using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Clients;

/// <summary>
/// Client for API-Football (v3.api-sports.io) with built-in daily quota protection.
/// Only used for transfers (and to look up a team's API-Football id): the free plan blocks the
/// current season for injuries/fixtures, which come from ESPN instead.
/// </summary>
public sealed class ApiFootballClient(
    HttpClient http,
    QuotaManager quota,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<ApiFootballClient> logger)
{
    private const string Provider = QuotaManager.ApiFootballProvider;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<ApiFootballTransferEnvelope>> GetTransfersByTeamAsync(
        int teamExternalId, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballTransferEnvelope>($"transfers?team={teamExternalId}", ct);
        return resp ?? [];
    }

    /// <summary>Searches teams by name (API-Football requires at least 3 characters). Costs 1 request.</summary>
    public async Task<IReadOnlyList<ApiFootballTeamSearchItem>> SearchTeamsAsync(
        string name, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballTeamSearchItem>($"teams?search={Uri.EscapeDataString(name)}", ct);
        return resp ?? [];
    }

    private async Task<List<T>?> SendAsync<T>(string relativeUrl, CancellationToken ct)
    {
        var key = apiKeys.Value.ApiFootball;
        if (string.IsNullOrWhiteSpace(key))
        {
            logger.LogDebug("ApiFootball key is not configured; skipping {Url}", relativeUrl);
            return null;
        }

        if (!await quota.CanConsumeAsync(Provider, collectorOptions.Value.ApiFootballDailyLimit, ct))
        {
            return null;
        }

        // Count the request before sending: a timeout or dropped connection may still have
        // consumed quota on the provider side, so under-counting is the riskier error.
        await quota.IncrementAsync(Provider, 1, ct);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
            req.Headers.TryAddWithoutValidation("x-apisports-key", key);

            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("ApiFootball returned {StatusCode} for {Url}", res.StatusCode, relativeUrl);
                return null;
            }

            var envelope = await res.Content.ReadFromJsonAsync<ApiFootballEnvelope<T>>(JsonOptions, ct);
            if (envelope is not null && HasErrors(envelope.Errors))
            {
                // API-Football reports plan/season/rate-limit problems with HTTP 200 and an "errors" field.
                logger.LogWarning("ApiFootball reported errors for {Url}: {Errors}", relativeUrl, envelope.Errors.GetRawText());
            }

            return envelope?.Response;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ApiFootball request failed for {Url}", relativeUrl);
            return null;
        }
    }

    private static bool HasErrors(JsonElement errors) => errors.ValueKind switch
    {
        JsonValueKind.Object => errors.EnumerateObject().Any(),
        JsonValueKind.Array => errors.GetArrayLength() > 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(errors.GetString()),
        _ => false
    };
}

public sealed record ApiFootballEnvelope<T>(
    [property: JsonPropertyName("response")] List<T>? Response,
    [property: JsonPropertyName("errors")] JsonElement Errors);

public sealed record ApiFootballNamedEntity(int Id, string Name, string? Photo, string? Logo);
public sealed record ApiFootballTransferEnvelope(
    ApiFootballNamedEntity Player,
    DateTimeOffset? Update,
    List<ApiFootballTransferDetail> Transfers);

public sealed record ApiFootballTransferDetail(
    string Date,
    string? Type,
    ApiFootballTransferTeams Teams);

public sealed record ApiFootballTransferTeams(ApiFootballNamedEntity In, ApiFootballNamedEntity Out);

public sealed record ApiFootballTeamSearchItem(ApiFootballTeamInfo Team);

public sealed record ApiFootballTeamInfo(int Id, string Name, string? Country, int? Founded, bool National, string? Logo);

