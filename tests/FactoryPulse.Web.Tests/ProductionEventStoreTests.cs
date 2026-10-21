using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryPulse.Web.Tests;

public sealed class ProductionEventStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 10, 21, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EventsAreWrittenAndAllValuesArePreserved()
    {
        using var database = new TemporaryDatabase();
        var store = CreateStore(database.Path);
        await store.StartAsync(CancellationToken.None);

        var expectedDuration = TimeSpan.FromSeconds(42.75);
        store.Enqueue(new ProductionEvent(
            "machine-01",
            InitialTime,
            ProductionEventType.FaultCleared,
            "SIM-101 — Simulated sensor fault",
            expectedDuration));
        await store.FlushAsync();

        var storedEvent = Assert.Single(await store.GetRecentEventsAsync());
        Assert.True(storedEvent.EventId > 0);
        Assert.Equal("machine-01", storedEvent.MachineId);
        Assert.Equal(InitialTime, storedEvent.Timestamp);
        Assert.Equal(ProductionEventType.FaultCleared, storedEvent.EventType);
        Assert.Equal("SIM-101 — Simulated sensor fault", storedEvent.Details);
        Assert.Equal(expectedDuration, storedEvent.Duration);

        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HistorySurvivesServiceRecreationAndNormalShutdownFlushesQueue()
    {
        using var database = new TemporaryDatabase();
        var firstStore = CreateStore(database.Path);
        await firstStore.StartAsync(CancellationToken.None);
        firstStore.Enqueue(CreateEvent(ProductionEventType.MachineStopped, InitialTime));
        await firstStore.StopAsync(CancellationToken.None);

        var recreatedStore = CreateStore(database.Path);
        await recreatedStore.StartAsync(CancellationToken.None);

        var storedEvent = Assert.Single(await recreatedStore.GetRecentEventsAsync());
        Assert.Equal(ProductionEventType.MachineStopped, storedEvent.EventType);
        Assert.Equal(InitialTime, storedEvent.Timestamp);

        await recreatedStore.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EventsAreOrderedByTimestampThenEventId()
    {
        using var database = new TemporaryDatabase();
        var store = CreateStore(database.Path);
        await store.StartAsync(CancellationToken.None);

        store.Enqueue(CreateEvent(ProductionEventType.MachineStarted, InitialTime));
        store.Enqueue(CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddMinutes(1)));
        store.Enqueue(CreateEvent(ProductionEventType.MachineStopped, InitialTime.AddMinutes(1)));
        await store.FlushAsync();

        var storedEvents = await store.GetRecentEventsAsync();

        Assert.Equal(
            [
                ProductionEventType.MachineStopped,
                ProductionEventType.UnitCompleted,
                ProductionEventType.MachineStarted
            ],
            storedEvents.Select(machineEvent => machineEvent.EventType));
        Assert.True(storedEvents[0].EventId > storedEvents[1].EventId);

        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MultipleSequentialWritesDoNotLoseRecords()
    {
        using var database = new TemporaryDatabase();
        var store = CreateStore(database.Path);
        await store.StartAsync(CancellationToken.None);

        for (var index = 0; index < 50; index++)
        {
            store.Enqueue(CreateEvent(
                ProductionEventType.UnitCompleted,
                InitialTime.AddSeconds(index)));
        }

        await store.FlushAsync();
        var storedEvents = await store.GetRecentEventsAsync(100);

        Assert.Equal(50, storedEvents.Count);
        Assert.Equal(50, storedEvents.Select(machineEvent => machineEvent.EventId).Distinct().Count());

        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MachineSimulationPersistsProductionAndOperatorEvents()
    {
        using var database = new TemporaryDatabase();
        var store = CreateStore(database.Path);
        await store.StartAsync(CancellationToken.None);
        var timeProvider = new ManualTimeProvider(InitialTime);
        using var simulator = new MachineSimulator(
            timeProvider,
            store,
            NullLogger<MachineSimulator>.Instance);

        Assert.True(simulator.SimulateProductionCycle());
        Assert.True(simulator.TryStop());
        Assert.True(simulator.TryStart());
        Assert.True(simulator.TryInjectFault());
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        Assert.True(simulator.TryClearFault());
        await store.FlushAsync();

        var storedEvents = await store.GetRecentEventsAsync();
        Assert.Equal(5, storedEvents.Count);
        Assert.Contains(storedEvents, machineEvent =>
            machineEvent.EventType == ProductionEventType.UnitCompleted);
        Assert.Contains(storedEvents, machineEvent =>
            machineEvent.EventType == ProductionEventType.MachineStarted);
        Assert.Contains(storedEvents, machineEvent =>
            machineEvent.EventType == ProductionEventType.MachineStopped);
        Assert.Contains(storedEvents, machineEvent =>
            machineEvent.EventType == ProductionEventType.FaultRaised);
        var clearedEvent = Assert.Single(storedEvents, machineEvent =>
            machineEvent.EventType == ProductionEventType.FaultCleared);
        Assert.Equal(TimeSpan.FromSeconds(15), clearedEvent.Duration);

        await store.StopAsync(CancellationToken.None);
    }

    private static ProductionEvent CreateEvent(
        ProductionEventType eventType,
        DateTimeOffset timestamp) =>
        new("machine-01", timestamp, eventType, eventType.ToString());

    private static ProductionEventStore CreateStore(string databasePath)
    {
        var options = new DbContextOptionsBuilder<FactoryPulseDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new ProductionEventStore(
            new TestDbContextFactory(options),
            NullLogger<ProductionEventStore>.Instance);
    }

    private sealed class TestDbContextFactory(
        DbContextOptions<FactoryPulseDbContext> options)
        : IDbContextFactory<FactoryPulseDbContext>
    {
        public FactoryPulseDbContext CreateDbContext() => new(options);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "FactoryPulseTests",
            Guid.NewGuid().ToString("N"));

        public TemporaryDatabase() => Directory.CreateDirectory(_directory);

        public string Path => System.IO.Path.Combine(_directory, "events.db");

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
