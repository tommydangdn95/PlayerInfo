using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PlayerInfo.Api.Configuration;
using PlayerInfo.Api.Services;

namespace PlayerInfo.Api.Clients;

/// <summary>
/// Client for API-Football (v3.api-sports.io) with built-in daily quota protection.
/// </summary>
public sealed class ApiFootballClient(
    HttpClient http,
    QuotaManager quota,
    IOptions<ApiKeysOptions> apiKeys,
    IOptions<CollectorOptions> collectorOptions,
    ILogger<ApiFootballClient> logger)
{
    private const string Provider = "ApiFootball";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<ApiFootballInjuryItem>> GetInjuriesByLeagueAsync(
        int leagueId, int season, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballInjuryItem>($"injuries?league={leagueId}&season={season}", ct);
        return resp ?? [];
    }

    public async Task<IReadOnlyList<ApiFootballFixtureItem>> GetFixturesByDateAsync(
        DateOnly date, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballFixtureItem>($"fixtures?date={date:yyyy-MM-dd}", ct);
        return resp ?? [];
    }

    public async Task<IReadOnlyList<ApiFootballTransferEnvelope>> GetTransfersByTeamAsync(
        int teamExternalId, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballTransferEnvelope>($"transfers?team={teamExternalId}", ct);
        return resp ?? [];
    }

    public async Task<IReadOnlyList<ApiFootballSquadEnvelope>> GetSquadByTeamAsync(
        int teamExternalId, CancellationToken ct = default)
    {
        var resp = await SendAsync<ApiFootballSquadEnvelope>($"players/squads?team={teamExternalId}", ct);
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

        using var req = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        req.Headers.TryAddWithoutValidation("x-apisports-key", key);

        using var res = await http.SendAsync(req, ct);
        await quota.IncrementAsync(Provider, 1, ct);

        if (!res.IsSuccessStatusCode)
        {
            logger.LogWarning("ApiFootball returned {StatusCode} for {Url}", res.StatusCode, relativeUrl);
            return null;
        }

        var envelope = await res.Content.ReadFromJsonAsync<ApiFootballEnvelope<T>>(JsonOptions, ct);
        return envelope?.Response;
    }
}

public sealed record ApiFootballEnvelope<T>([property: JsonPropertyName("response")] List<T>? Response);

public sealed record ApiFootballInjuryItem(
    ApiFootballNamedEntity Player,
    ApiFootballNamedEntity Team,
    ApiFootballFixtureRef Fixture,
    ApiFootballInjuryDetails? Injury);

public sealed record ApiFootballNamedEntity(int Id, string Name, string? Photo, string? Logo);
public sealed record ApiFootballFixtureRef(int Id, DateTime Date);
public sealed record ApiFootballInjuryDetails(string? Type, string? Reason);

public sealed record ApiFootballFixtureItem(
    ApiFootballFixtureMeta Fixture,
    ApiFootballLeagueMeta League,
    ApiFootballTeamsPair Teams,
    ApiFootballGoalsPair Goals);

public sealed record ApiFootballFixtureMeta(int Id, DateTime Date, ApiFootballFixtureStatus Status);
public sealed record ApiFootballFixtureStatus(string Short);
public sealed record ApiFootballLeagueMeta(int Id, string Name);
public sealed record ApiFootballTeamsPair(ApiFootballNamedEntity Home, ApiFootballNamedEntity Away);
public sealed record ApiFootballGoalsPair(int? Home, int? Away);

public sealed record ApiFootballTransferEnvelope(
    ApiFootballNamedEntity Player,
    DateTime Update,
    List<ApiFootballTransferDetail> Transfers);

public sealed record ApiFootballTransferDetail(
    string Date,
    string? Type,
    ApiFootballTransferTeams Teams);

public sealed record ApiFootballTransferTeams(ApiFootballNamedEntity In, ApiFootballNamedEntity Out);

public sealed record ApiFootballSquadEnvelope(
    ApiFootballNamedEntity Team,
    List<ApiFootballSquadPlayer> Players);

public sealed record ApiFootballSquadPlayer(int Id, string Name, int? Number, string? Position, string? Photo);
