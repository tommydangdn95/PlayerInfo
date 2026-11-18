namespace PlayerInfo.Api.Data.Entities;

public sealed class PlayerInjury
{
    public long Id { get; set; }
    public int PlayerExternalId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int TeamExternalId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? Type { get; set; }
    public DateTime ReportedDateUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class PlayerTransfer
{
    public long Id { get; set; }
    public int PlayerExternalId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int? FromTeamExternalId { get; set; }
    public string FromTeamName { get; set; } = string.Empty;
    public int? ToTeamExternalId { get; set; }
    public string ToTeamName { get; set; } = string.Empty;
    public string? TransferType { get; set; }
    public DateTime TransferDateUtc { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Fixture
{
    public long Id { get; set; }
    public int ExternalFixtureId { get; set; }
    public int LeagueExternalId { get; set; }
    public string LeagueName { get; set; } = string.Empty;
    public string HomeTeamName { get; set; } = string.Empty;
    public string AwayTeamName { get; set; } = string.Empty;
    public int? HomeGoals { get; set; }
    public int? AwayGoals { get; set; }
    public string StatusShort { get; set; } = string.Empty;
    public DateTime KickoffAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ApiQuotaLog
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty; // "ApiFootball", "NewsData", "Gemini"
    public DateOnly LogDate { get; set; }
    public int RequestCount { get; set; }
}
