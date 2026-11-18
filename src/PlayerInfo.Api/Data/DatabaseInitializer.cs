using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PlayerInfo.Api.Data;

/// <summary>
/// Brings the database schema up to date on startup.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational())
        {
            // InMemory provider (local development without a connection string).
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }

        await BaselineLegacySchemaAsync(db, logger, ct);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Databases created by the earlier EnsureCreated() startup already contain the
    /// InitialCreate schema but have no migrations history. Record InitialCreate as applied
    /// for them so Migrate() does not try to create existing tables.
    /// </summary>
    private static async Task BaselineLegacySchemaAsync(AppDbContext db, ILogger logger, CancellationToken ct)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        if (applied.Any())
        {
            return;
        }

        var teamsTableExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('public.\"Teams\"') IS NOT NULL AS \"Value\"")
            .SingleAsync(ct);
        if (!teamsTableExists)
        {
            return;
        }

        var initialMigration = db.Database.GetMigrations().First();
        var history = db.GetService<IHistoryRepository>();
        var efVersion = typeof(DbContext).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "9.0.0";

        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(initialMigration, efVersion)), ct);

        logger.LogWarning("Existing schema without migrations history detected; marked {Migration} as applied", initialMigration);
    }
}

/// <summary>
/// Used only by `dotnet ef` at design time, so migrations can be generated without a real database.
/// </summary>
public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=playerinfo_design;Username=postgres;Password=postgres")
            .Options);
}
