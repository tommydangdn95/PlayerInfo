using Microsoft.EntityFrameworkCore;
using PlayerInfo.Api.Data.Entities;

namespace PlayerInfo.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<WatchlistAlias> WatchlistAliases => Set<WatchlistAlias>();
    public DbSet<NewsArticle> NewsArticles => Set<NewsArticle>();
    public DbSet<ArticleEntity> ArticleEntities => Set<ArticleEntity>();
    public DbSet<Story> Stories => Set<Story>();
    public DbSet<NewsSource> NewsSources => Set<NewsSource>();
    public DbSet<PlayerInjury> PlayerInjuries => Set<PlayerInjury>();
    public DbSet<PlayerTransfer> PlayerTransfers => Set<PlayerTransfer>();
    public DbSet<Fixture> Fixtures => Set<Fixture>();
    public DbSet<ApiQuotaLog> ApiQuotaLogs => Set<ApiQuotaLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(e =>
        {
            // Unique ids; PostgreSQL allows several NULLs in a unique index.
            e.HasIndex(x => x.EspnId).IsUnique();
            e.HasIndex(x => x.ApiFootballId).IsUnique();
            e.Property(x => x.EspnId).HasMaxLength(20);
            e.Property(x => x.LeagueSlug).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Player>(e =>
        {
            e.HasIndex(x => x.EspnId).IsUnique();
            e.Property(x => x.EspnId).HasMaxLength(20);
            e.Property(x => x.FullName).HasMaxLength(200);
            e.HasOne(x => x.Team)
                .WithMany(t => t.Players)
                .HasForeignKey(x => x.TeamId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WatchlistAlias>(e =>
        {
            e.HasIndex(x => new { x.EntityType, x.EntityId });
            e.Property(x => x.EntityType).HasMaxLength(20);
            e.Property(x => x.Keyword).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(10);
        });

        modelBuilder.Entity<NewsArticle>(e =>
        {
            e.HasIndex(x => x.UrlHash).IsUnique();
            e.HasIndex(x => x.PublishedAtUtc);
            e.Property(x => x.UrlHash).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.SourceName).HasMaxLength(150);
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.ClaimType).HasMaxLength(20);
            e.Property(x => x.SourceTier).HasDefaultValue(3);
            e.HasOne(x => x.Story)
                .WithMany(s => s.Articles)
                .HasForeignKey(x => x.StoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Story>(e =>
        {
            e.HasIndex(x => x.LastUpdatedUtc);
            e.Property(x => x.HeadlineVi).HasMaxLength(300);
            e.Property(x => x.Topic).HasMaxLength(50);
            e.Property(x => x.Confidence).HasMaxLength(20);
        });

        modelBuilder.Entity<NewsSource>(e =>
        {
            e.HasIndex(x => x.Pattern).IsUnique();
            e.Property(x => x.Pattern).HasMaxLength(100);
        });

        modelBuilder.Entity<ArticleEntity>(e =>
        {
            e.HasKey(x => new { x.ArticleId, x.EntityType, x.EntityId });
            e.Property(x => x.EntityType).HasMaxLength(20);
            e.HasOne(x => x.Article)
                .WithMany(a => a.Entities)
                .HasForeignKey(x => x.ArticleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlayerInjury>(e =>
        {
            e.HasIndex(x => new { x.PlayerEspnId, x.ReportedDateUtc });
            e.Property(x => x.PlayerEspnId).HasMaxLength(20);
            e.Property(x => x.TeamEspnId).HasMaxLength(20);
            e.Property(x => x.PlayerName).HasMaxLength(200);
            e.Property(x => x.TeamName).HasMaxLength(200);
        });

        modelBuilder.Entity<PlayerTransfer>(e =>
        {
            e.HasIndex(x => new { x.PlayerExternalId, x.TransferDateUtc });
            e.Property(x => x.PlayerName).HasMaxLength(200);
        });

        modelBuilder.Entity<Fixture>(e =>
        {
            e.HasIndex(x => x.EspnEventId).IsUnique();
            e.HasIndex(x => x.KickoffAtUtc);
            e.Property(x => x.EspnEventId).HasMaxLength(20);
            e.Property(x => x.LeagueSlug).HasMaxLength(50);
            e.Property(x => x.HomeTeamEspnId).HasMaxLength(20);
            e.Property(x => x.AwayTeamEspnId).HasMaxLength(20);
            e.Property(x => x.StatusShort).HasMaxLength(50);
        });

        modelBuilder.Entity<ApiQuotaLog>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.LogDate }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(50);
        });
    }
}
