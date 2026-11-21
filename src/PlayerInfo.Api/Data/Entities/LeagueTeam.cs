namespace PlayerInfo.Api.Data.Entities;

/// <summary>
/// A team of a configured league as listed by ESPN, cached so the admin "Đội bóng" page does not call ESPN
/// on every visit. Teams are fixed for a season: refreshed when a new season starts, or on demand.
/// </summary>
public sealed class LeagueTeam
{
    public int Id { get; set; }
    public string LeagueSlug { get; set; } = string.Empty;
    public string EspnId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public string? LogoUrl { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
