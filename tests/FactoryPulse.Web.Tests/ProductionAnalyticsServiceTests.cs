using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryPulse.Web.Tests;

public sealed class ProductionAnalyticsServiceTests
{
    private const string MachineId = "machine-01";
    private static readonly DateTimeOffset ReportTime =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ProductionTotalsRateAndBucketsUseExactPeriodBoundaries()
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        var sessionId = testContext.Session.Id;
        await testContext.SeedAsync(
            Event(ProductionEventType.MonitoringStarted, ReportTime.AddHours(-1), sessionId),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddHours(-1)),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddMinutes(-55)),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddTicks(-1)),
            Event(ProductionEventType.UnitCompleted, ReportTime));

        var result = await testContext.Service.GetAnalyticsAsync(
            MachineId,
            AnalyticsPeriod.LastHour);

        Assert.Equal(3, result.TotalProduction);
        Assert.Equal(3, result.ProductionRatePerHour);
        Assert.Equal(12, result.ProductionBuckets.Count);
        Assert.Equal(3, result.ProductionBuckets.Sum(bucket => bucket.UnitCount));
        Assert.True(result.HasCompleteMonitoringCoverage);
        Assert.Equal(100, result.AvailabilityPercent);
        Assert.Null(result.MeanTimeBetweenFailures);
        Assert.Contains("no failures", result.MeanTimeBetweenFailuresUnavailableReason);
    }

    [Fact]
    public async Task FaultIntervalsAreClippedAndActiveFaultRunsToReportTime()
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        await testContext.SeedAsync(
            Event(
                ProductionEventType.MonitoringStarted,
                ReportTime.AddHours(-1),
                testContext.Session.Id),
            Event(ProductionEventType.FaultRaised, ReportTime.AddMinutes(-70)),
            Event(
                ProductionEventType.FaultCleared,
                ReportTime.AddMinutes(-50),
                duration: TimeSpan.FromMinutes(20)),
            Event(ProductionEventType.FaultRaised, ReportTime.AddMinutes(-20)));

        var result = await testContext.Service.GetAnalyticsAsync(
            MachineId,
            AnalyticsPeriod.LastHour);

        Assert.Equal(1, result.FaultCount);
        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalFaultDowntime);
        Assert.Equal(50, result.AvailabilityPercent);
        Assert.Equal(TimeSpan.FromMinutes(20), result.MeanTimeToRepair);
        Assert.Equal(TimeSpan.FromMinutes(30), result.MeanTimeBetweenFailures);
    }

    [Fact]
    public async Task MttrAveragesOnlyFaultsClearedInsideThePeriod()
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        await testContext.SeedAsync(
            Event(
                ProductionEventType.MonitoringStarted,
                ReportTime.AddHours(-1),
                testContext.Session.Id),
            Event(ProductionEventType.FaultRaised, ReportTime.AddMinutes(-50)),
            Event(
                ProductionEventType.FaultCleared,
                ReportTime.AddMinutes(-40),
                duration: TimeSpan.FromMinutes(10)),
            Event(ProductionEventType.FaultRaised, ReportTime.AddMinutes(-30)),
            Event(
                ProductionEventType.FaultCleared,
                ReportTime.AddMinutes(-10),
                duration: TimeSpan.FromMinutes(20)));

        var result = await testContext.Service.GetAnalyticsAsync(
            MachineId,
            AnalyticsPeriod.LastHour);

        Assert.Equal(2, result.FaultCount);
        Assert.Equal(TimeSpan.FromMinutes(30), result.TotalFaultDowntime);
        Assert.Equal(TimeSpan.FromMinutes(15), result.MeanTimeToRepair);
        Assert.Equal(TimeSpan.FromMinutes(15), result.MeanTimeBetweenFailures);
    }

    [Fact]
    public async Task RestartGapMakesAvailabilityAndMtbfUnavailable()
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        await testContext.SeedAsync(
            Event(ProductionEventType.MonitoringStarted, ReportTime.AddHours(-2), "old"),
            Event(ProductionEventType.MonitoringStopped, ReportTime.AddMinutes(-50), "old"),
            Event(
                ProductionEventType.MonitoringStarted,
                ReportTime.AddMinutes(-30),
                testContext.Session.Id),
            Event(ProductionEventType.FaultRaised, ReportTime.AddMinutes(-20)),
            Event(
                ProductionEventType.FaultCleared,
                ReportTime.AddMinutes(-15),
                duration: TimeSpan.FromMinutes(5)));

        var result = await testContext.Service.GetAnalyticsAsync(
            MachineId,
            AnalyticsPeriod.LastHour);

        Assert.False(result.HasCompleteMonitoringCoverage);
        Assert.Equal(TimeSpan.FromMinutes(40), result.ConfirmedMonitoringDuration);
        Assert.Null(result.AvailabilityPercent);
        Assert.Contains("offline", result.AvailabilityUnavailableReason);
        Assert.Null(result.MeanTimeBetweenFailures);
        Assert.NotNull(result.MeanTimeToRepair);
    }

    [Fact]
    public async Task InterruptedHistoricalSessionIsNotAssumedToCoverOfflineTime()
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        await testContext.SeedAsync(
            Event(ProductionEventType.MonitoringStarted, ReportTime.AddMinutes(-55), "crashed"),
            Event(
                ProductionEventType.MonitoringStarted,
                ReportTime.AddMinutes(-10),
                testContext.Session.Id));

        var result = await testContext.Service.GetAnalyticsAsync(
            MachineId,
            AnalyticsPeriod.LastHour);

        Assert.Equal(TimeSpan.FromMinutes(10), result.ConfirmedMonitoringDuration);
        Assert.False(result.HasCompleteMonitoringCoverage);
        Assert.Null(result.AvailabilityPercent);
    }

    [Theory]
    [InlineData(AnalyticsPeriod.LastHour, 1)]
    [InlineData(AnalyticsPeriod.Last24Hours, 2)]
    [InlineData(AnalyticsPeriod.Last7Days, 3)]
    public async Task PeriodSelectionChangesPersistedProductionWindow(
        AnalyticsPeriod period,
        long expectedCount)
    {
        using var database = new TemporaryDatabase();
        var testContext = await CreateTestContextAsync(database.Path);
        await testContext.SeedAsync(
            Event(ProductionEventType.UnitCompleted, ReportTime.AddMinutes(-30)),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddHours(-12)),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddDays(-3)),
            Event(ProductionEventType.UnitCompleted, ReportTime.AddDays(-8)));

        var result = await testContext.Service.GetAnalyticsAsync(MachineId, period);

        Assert.Equal(expectedCount, result.TotalProduction);
    }

    private static ProductionEvent Event(
        ProductionEventType eventType,
        DateTimeOffset timestamp,
        string? details = null,
        TimeSpan? duration = null) =>
        new(MachineId, timestamp, eventType, details ?? eventType.ToString(), duration);

    private static async Task<TestContext> CreateTestContextAsync(string databasePath)
    {
        var options = new DbContextOptionsBuilder<FactoryPulseDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        var factory = new TestDbContextFactory(options);
        await using (var dbContext = await factory.CreateDbContextAsync())
        {
            await dbContext.Database.EnsureCreatedAsync();
        }

        var timeProvider = new FixedTimeProvider(ReportTime);
        var session = new MonitoringSession(timeProvider);
        return new TestContext(
            factory,
            session,
            new ProductionAnalyticsService(
                factory,
                timeProvider,
                session,
                NullLogger<ProductionAnalyticsService>.Instance));
    }

    private sealed record TestContext(
        TestDbContextFactory Factory,
        MonitoringSession Session,
        ProductionAnalyticsService Service)
    {
        public async Task SeedAsync(params ProductionEvent[] events)
        {
            await using var dbContext = await Factory.CreateDbContextAsync();
            dbContext.ProductionEvents.AddRange(events.Select(
                PersistedProductionEvent.FromModel));
            await dbContext.SaveChangesAsync();
        }
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<FactoryPulseDbContext> options)
        : IDbContextFactory<FactoryPulseDbContext>
    {
        public FactoryPulseDbContext CreateDbContext() => new(options);

        public Task<FactoryPulseDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FactoryPulseAnalyticsTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase() => Directory.CreateDirectory(_directory);

        public string Path => System.IO.Path.Combine(_directory, "analytics.db");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
