using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryPulse.Web.Tests;

public sealed class MachineSimulatorTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewSimulatorStartsMachineRunningWithZeroCountsAndNoFault()
    {
        using var simulator = CreateSimulator();

        var snapshot = simulator.GetMachineSnapshot();

        Assert.Equal("machine-01", snapshot.MachineId);
        Assert.Equal("Machine 01", snapshot.MachineName);
        Assert.Equal(MachineState.Running, snapshot.CurrentState);
        Assert.Equal(0, snapshot.CompletedUnitCount);
        Assert.Equal(0, snapshot.GoodUnitCount);
        Assert.Equal(0, snapshot.RejectedUnitCount);
        Assert.Null(snapshot.ActiveFault);
        Assert.Empty(simulator.GetRecentEvents());
    }

    [Fact]
    public void ValidCommandsApplyRequiredStateTransitionsAndEvents()
    {
        using var simulator = CreateSimulator();

        Assert.True(simulator.TryStop());
        Assert.Equal(MachineState.Idle, simulator.GetMachineSnapshot().CurrentState);

        Assert.True(simulator.TryStart());
        Assert.Equal(MachineState.Running, simulator.GetMachineSnapshot().CurrentState);

        Assert.True(simulator.TryInjectFault());
        var faultedSnapshot = simulator.GetMachineSnapshot();
        Assert.Equal(MachineState.Faulted, faultedSnapshot.CurrentState);
        Assert.NotNull(faultedSnapshot.ActiveFault);

        Assert.True(simulator.TryClearFault());
        var clearedSnapshot = simulator.GetMachineSnapshot();
        Assert.Equal(MachineState.Idle, clearedSnapshot.CurrentState);
        Assert.Null(clearedSnapshot.ActiveFault);

        Assert.Equal(
            [
                ProductionEventType.FaultCleared,
                ProductionEventType.FaultRaised,
                ProductionEventType.MachineStarted,
                ProductionEventType.MachineStopped
            ],
            simulator.GetRecentEvents().Select(machineEvent => machineEvent.EventType));
    }

    [Fact]
    public void InvalidCommandsDoNotChangeStateOrGenerateEvents()
    {
        using var simulator = CreateSimulator();

        Assert.False(simulator.TryStart());
        Assert.False(simulator.TryClearFault());
        Assert.Empty(simulator.GetRecentEvents());

        Assert.True(simulator.TryStop());
        Assert.False(simulator.TryStop());
        Assert.True(simulator.TryInjectFault());
        Assert.False(simulator.TryInjectFault());
        Assert.False(simulator.TryStart());
        Assert.False(simulator.TryStop());

        Assert.Equal(MachineState.Faulted, simulator.GetMachineSnapshot().CurrentState);
        Assert.Equal(2, simulator.GetRecentEvents().Count);
    }

    [Fact]
    public void ProductionOccursOnlyWhileRunningAndCountersRemainConsistent()
    {
        using var simulator = CreateSimulator();

        Assert.True(simulator.SimulateProductionCycle());

        Assert.True(simulator.TryStop());
        Assert.False(simulator.SimulateProductionCycle());

        Assert.True(simulator.TryStart());
        Assert.True(simulator.SimulateProductionCycle());

        Assert.True(simulator.TryInjectFault());
        Assert.False(simulator.SimulateProductionCycle());

        Assert.True(simulator.TryClearFault());
        Assert.Equal(MachineState.Idle, simulator.GetMachineSnapshot().CurrentState);
        Assert.False(simulator.SimulateProductionCycle());

        Assert.True(simulator.TryStart());
        Assert.True(simulator.SimulateProductionCycle());

        var snapshot = simulator.GetMachineSnapshot();
        Assert.Equal(3, snapshot.CompletedUnitCount);
        Assert.Equal(3, snapshot.GoodUnitCount);
        Assert.Equal(0, snapshot.RejectedUnitCount);
        Assert.Equal(
            snapshot.CompletedUnitCount,
            snapshot.GoodUnitCount + snapshot.RejectedUnitCount);
    }

    [Fact]
    public void FaultTimestampsAndFinalDurationUseProvidedTime()
    {
        var timeProvider = new ManualTimeProvider(InitialTime);
        using var simulator = CreateSimulator(timeProvider);

        Assert.True(simulator.TryInjectFault());

        var raisedSnapshot = simulator.GetMachineSnapshot();
        var raisedEvent = Assert.Single(simulator.GetRecentEvents());
        Assert.Equal(MachineSimulator.SimulatedFaultCode, raisedSnapshot.ActiveFault?.Code);
        Assert.Equal(MachineSimulator.SimulatedFaultDescription, raisedSnapshot.ActiveFault?.Description);
        Assert.Equal(InitialTime, raisedSnapshot.ActiveFault?.StartedAtUtc);
        Assert.Equal(TimeSpan.Zero, raisedSnapshot.ActiveFault?.ElapsedDuration);
        Assert.Equal(InitialTime, raisedEvent.Timestamp);
        Assert.Equal(ProductionEventType.FaultRaised, raisedEvent.EventType);

        var expectedDuration = TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(23.456);
        timeProvider.Advance(expectedDuration);

        Assert.Equal(expectedDuration, simulator.GetMachineSnapshot().ActiveFault?.ElapsedDuration);
        Assert.True(simulator.TryClearFault());

        var clearedSnapshot = simulator.GetMachineSnapshot();
        var clearedEvent = simulator.GetRecentEvents()[0];
        Assert.Equal(MachineState.Idle, clearedSnapshot.CurrentState);
        Assert.Null(clearedSnapshot.ActiveFault);
        Assert.Equal(InitialTime + expectedDuration, clearedEvent.Timestamp);
        Assert.Equal(ProductionEventType.FaultCleared, clearedEvent.EventType);
        Assert.Equal(expectedDuration, clearedEvent.Duration);
        Assert.False(simulator.SimulateProductionCycle());
    }

    [Fact]
    public void RecentEventsRetainOnlyTheLatestTwentyEntries()
    {
        using var simulator = CreateSimulator();

        for (var cycle = 0; cycle < MachineSimulator.MaximumRecentEventCount + 5; cycle++)
        {
            simulator.SimulateProductionCycle();
        }

        var snapshot = simulator.GetMachineSnapshot();
        var events = simulator.GetRecentEvents();

        Assert.Equal(25, snapshot.CompletedUnitCount);
        Assert.Equal(MachineSimulator.MaximumRecentEventCount, events.Count);
        Assert.All(events, machineEvent =>
            Assert.Equal(ProductionEventType.UnitCompleted, machineEvent.EventType));
    }

    private static MachineSimulator CreateSimulator(TimeProvider? timeProvider = null) =>
        new(timeProvider ?? new ManualTimeProvider(InitialTime),
            new RecordingEventStore(),
            NullLogger<MachineSimulator>.Instance);

    private sealed class RecordingEventStore : IProductionEventStore
    {
        public List<ProductionEvent> Events { get; } = [];

        public void Enqueue(ProductionEvent machineEvent) => Events.Add(machineEvent);

        public Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
            int count = 20,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProductionEvent>>(
                Events.TakeLast(count).Reverse<ProductionEvent>().ToArray());

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialTime;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
