using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FactoryPulse.Web.Tests;

public sealed class DatabaseMigrationTests
{
    private const string InitialMigration =
        "20261021153846_InitialEventPersistence";
    private const string AnalyticsMigration =
        "20261021174500_AddAnalyticsEventIndex";

    [Fact]
    public async Task MigrationsCreateANewSqliteDatabase()
    {
        using var database = new TemporaryDatabase();
        await using var dbContext = CreateDbContext(database.Path);

        await dbContext.Database.MigrateAsync();

        Assert.Equal(
            [InitialMigration, AnalyticsMigration],
            await dbContext.Database.GetAppliedMigrationsAsync());
        Assert.False(await dbContext.ProductionEvents.AnyAsync());
    }

    [Fact]
    public async Task AnalyticsMigrationUpgradesExistingHistoryWithoutDataLoss()
    {
        using var database = new TemporaryDatabase();
        var timestamp = new DateTimeOffset(
            2026, 10, 21, 18, 30, 0, TimeSpan.Zero);

        await using (var oldContext = CreateDbContext(database.Path))
        {
            var migrator = oldContext.GetService<IMigrator>();
            await migrator.MigrateAsync(InitialMigration);
            oldContext.ProductionEvents.Add(PersistedProductionEvent.FromModel(
                new ProductionEvent(
                    "machine-01",
                    timestamp,
                    ProductionEventType.UnitCompleted,
                    "Existing event")));
            await oldContext.SaveChangesAsync();
        }

        await using (var upgradedContext = CreateDbContext(database.Path))
        {
            await upgradedContext.Database.MigrateAsync();

            var storedEvent = Assert.Single(await upgradedContext.ProductionEvents
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal("machine-01", storedEvent.MachineId);
            Assert.Equal(timestamp.UtcDateTime, storedEvent.TimestampUtc);
            Assert.Equal(ProductionEventType.UnitCompleted, storedEvent.EventType);
            Assert.Equal("Existing event", storedEvent.Details);
            Assert.Equal(
                [InitialMigration, AnalyticsMigration],
                await upgradedContext.Database.GetAppliedMigrationsAsync());
        }
    }

    private static FactoryPulseDbContext CreateDbContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<FactoryPulseDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new FactoryPulseDbContext(options);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FactoryPulseMigrationTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase() => Directory.CreateDirectory(_directory);

        public string Path => System.IO.Path.Combine(_directory, "migrations.db");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
