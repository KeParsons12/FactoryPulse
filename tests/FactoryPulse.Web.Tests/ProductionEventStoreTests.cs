using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
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
        Assert.True(store.TryEnqueue(new ProductionEvent(
            "machine-01",
            InitialTime,
            ProductionEventType.FaultCleared,
            "SIM-101 — Simulated sensor fault",
            expectedDuration)));
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
        Assert.True(firstStore.TryEnqueue(
            CreateEvent(ProductionEventType.MachineStopped, InitialTime)));
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

        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.MachineStarted, InitialTime)));
        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddMinutes(1))));
        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.MachineStopped, InitialTime.AddMinutes(1))));
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
            Assert.True(store.TryEnqueue(CreateEvent(
                ProductionEventType.UnitCompleted,
                InitialTime.AddSeconds(index))));
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

        Assert.True(simulator.TryStart());
        Assert.True(simulator.SimulateProductionCycle());
        Assert.True(simulator.TryStop());
        Assert.True(simulator.TryStart());
        Assert.True(simulator.TryInjectFault());
        timeProvider.Advance(TimeSpan.FromSeconds(15));
        Assert.True(simulator.TryClearFault());
        await store.FlushAsync();

        var storedEvents = await store.GetRecentEventsAsync();
        Assert.Equal(6, storedEvents.Count);
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

    [Fact]
    public async Task UnclearedFaultIsRestoredAfterApplicationRestart()
    {
        using var database = new TemporaryDatabase();
        var firstStore = CreateStore(database.Path);
        await firstStore.StartAsync(CancellationToken.None);
        using (var firstSimulator = new MachineSimulator(
                   new ManualTimeProvider(InitialTime),
                   firstStore,
                   NullLogger<MachineSimulator>.Instance))
        {
            Assert.True(firstSimulator.TryInjectFault());
            await firstStore.FlushAsync();
        }

        await firstStore.StopAsync(CancellationToken.None);

        var restartTime = InitialTime.AddMinutes(5);
        var restartedStore = CreateStore(database.Path);
        await restartedStore.StartAsync(CancellationToken.None);
        using (var restartedSimulator = new MachineSimulator(
                   new ManualTimeProvider(restartTime),
                   restartedStore,
                   NullLogger<MachineSimulator>.Instance))
        {
            await restartedSimulator.StartAsync(CancellationToken.None);

            var restoredSnapshot = restartedSimulator.GetMachineSnapshot();
            Assert.Equal(MachineState.Faulted, restoredSnapshot.CurrentState);
            Assert.Equal(InitialTime, restoredSnapshot.ActiveFault?.StartedAtUtc);
            Assert.Equal(TimeSpan.FromMinutes(5), restoredSnapshot.ActiveFault?.ElapsedDuration);
            Assert.False(restartedSimulator.SimulateProductionCycle());
            Assert.True(restartedSimulator.TryClearFault());
            Assert.Equal(MachineState.Idle, restartedSimulator.GetMachineSnapshot().CurrentState);
            await restartedStore.FlushAsync();
            await restartedSimulator.StopAsync(CancellationToken.None);
        }

        await restartedStore.StopAsync(CancellationToken.None);

        var clearedStore = CreateStore(database.Path);
        await clearedStore.StartAsync(CancellationToken.None);
        using (var clearedSimulator = new MachineSimulator(
                   new ManualTimeProvider(restartTime),
                   clearedStore,
                   NullLogger<MachineSimulator>.Instance))
        {
            await clearedSimulator.StartAsync(CancellationToken.None);
            Assert.Equal(MachineState.Idle, clearedSimulator.GetMachineSnapshot().CurrentState);
            Assert.Null(clearedSimulator.GetMachineSnapshot().ActiveFault);
            await clearedSimulator.StopAsync(CancellationToken.None);
        }

        await clearedStore.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PermanentWriteFailureInterlocksSimulationAndBecomesObservable()
    {
        using var database = new TemporaryDatabase();
        var options = new ProductionEventStoreOptions
        {
            QueueCapacity = 4,
            MaximumWriteAttempts = 2,
            RetryDelay = TimeSpan.Zero
        };
        var store = CreateStore(
            database.Path,
            options,
            new FailingSaveChangesInterceptor(int.MaxValue));
        await store.StartAsync(CancellationToken.None);
        using var simulator = new MachineSimulator(
            new ManualTimeProvider(InitialTime),
            store,
            NullLogger<MachineSimulator>.Instance);

        Assert.True(simulator.TryStart());
        await Assert.ThrowsAsync<PersistenceUnavailableException>(() => store.FlushAsync());

        var health = store.GetHealthSnapshot();
        Assert.Equal(PersistenceHealthStatus.Unavailable, health.Status);
        Assert.Equal(1, health.PendingEventCount);
        Assert.NotNull(health.Message);
        Assert.False(simulator.SimulateProductionCycle());
        Assert.False(simulator.TryStop());
        Assert.Equal(0, simulator.GetMachineSnapshot().CompletedUnitCount);
        Assert.False(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddSeconds(1))));
        await Assert.ThrowsAnyAsync<Exception>(() => store.StopAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TransientWriteFailuresRecoverWithoutLosingTheEvent()
    {
        using var database = new TemporaryDatabase();
        var options = new ProductionEventStoreOptions
        {
            QueueCapacity = 4,
            MaximumWriteAttempts = 3,
            RetryDelay = TimeSpan.Zero
        };
        var store = CreateStore(
            database.Path,
            options,
            new FailingSaveChangesInterceptor(failuresBeforeSuccess: 2));
        await store.StartAsync(CancellationToken.None);

        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.MachineStarted, InitialTime)));
        await store.FlushAsync();

        var health = store.GetHealthSnapshot();
        Assert.Equal(PersistenceHealthStatus.Healthy, health.Status);
        Assert.Equal(0, health.PendingEventCount);
        Assert.Single(await store.GetRecentEventsAsync());
        await store.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task BoundedQueueRejectsOverflowAndRecoversAfterDrain()
    {
        using var database = new TemporaryDatabase();
        var interceptor = new BlockingSaveChangesInterceptor();
        var options = new ProductionEventStoreOptions
        {
            QueueCapacity = 3,
            MaximumWriteAttempts = 1,
            RetryDelay = TimeSpan.Zero
        };
        var store = CreateStore(database.Path, options, interceptor);
        await store.StartAsync(CancellationToken.None);

        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime)));
        await interceptor.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddSeconds(1))));
        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddSeconds(2))));
        Assert.False(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddSeconds(3))));

        var fullHealth = store.GetHealthSnapshot();
        Assert.Equal(PersistenceHealthStatus.Backpressured, fullHealth.Status);
        Assert.Equal(3, fullHealth.PendingEventCount);
        Assert.Equal(3, fullHealth.QueueCapacity);

        interceptor.AllowWrites.TrySetResult();
        await store.FlushAsync();

        var recoveredHealth = store.GetHealthSnapshot();
        Assert.Equal(PersistenceHealthStatus.Healthy, recoveredHealth.Status);
        Assert.Equal(0, recoveredHealth.PendingEventCount);
        Assert.Equal(3, (await store.GetRecentEventsAsync(10)).Count);

        Assert.True(store.TryEnqueue(
            CreateEvent(ProductionEventType.UnitCompleted, InitialTime.AddSeconds(4))));
        await store.FlushAsync();
        Assert.Equal(4, (await store.GetRecentEventsAsync(10)).Count);
        await store.StopAsync(CancellationToken.None);
    }

    private static ProductionEvent CreateEvent(
        ProductionEventType eventType,
        DateTimeOffset timestamp) =>
        new("machine-01", timestamp, eventType, eventType.ToString());

    private static ProductionEventStore CreateStore(
        string databasePath,
        ProductionEventStoreOptions? storeOptions = null,
        params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<FactoryPulseDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(interceptors)
            .Options;
        return new ProductionEventStore(
            new TestDbContextFactory(options),
            Options.Create(storeOptions ?? new ProductionEventStoreOptions()),
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

    private sealed class FailingSaveChangesInterceptor(int failuresBeforeSuccess)
        : SaveChangesInterceptor
    {
        private int _remainingFailures = failuresBeforeSuccess;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _remainingFailures) >= 0)
            {
                throw new InvalidOperationException("Deterministic database write failure.");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class BlockingSaveChangesInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource WriteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowWrites { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await AllowWrites.Task.WaitAsync(cancellationToken);
            return result;
        }
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
