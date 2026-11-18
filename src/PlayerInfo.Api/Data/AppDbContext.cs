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
    public DbSet<PlayerInjury> PlayerInjuries => Set<PlayerInjury>();
    public DbSet<PlayerTransfer> PlayerTransfers => Set<PlayerTransfer>();
    public DbSet<Fixture> Fixtures => Set<Fixture>();
    public DbSet<ApiQuotaLog> ApiQuotaLogs => Set<ApiQuotaLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Team>(e =>
        {
            e.HasIndex(x => x.ExternalApiId).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Player>(e =>
        {
            e.HasIndex(x => x.ExternalApiId).IsUnique();
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
            e.HasIndex(x => new { x.PlayerExternalId, x.ReportedDateUtc });
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
            e.HasIndex(x => x.ExternalFixtureId).IsUnique();
            e.HasIndex(x => x.KickoffAtUtc);
        });

        modelBuilder.Entity<ApiQuotaLog>(e =>
        {
            e.HasIndex(x => new { x.Provider, x.LogDate }).IsUnique();
            e.Property(x => x.Provider).HasMaxLength(50);
        });
    }
}
