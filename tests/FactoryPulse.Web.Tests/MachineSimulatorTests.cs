using FactoryPulse.Web.Models;
using FactoryPulse.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryPulse.Web.Tests;

public sealed class MachineSimulatorTests
{
    [Fact]
    public void NewSimulatorStartsMachineRunningWithZeroCounts()
    {
        using var simulator = CreateSimulator();

        var snapshot = simulator.GetMachineSnapshot();

        Assert.Equal("machine-01", snapshot.MachineId);
        Assert.Equal("Machine 01", snapshot.MachineName);
        Assert.Equal(MachineState.Running, snapshot.CurrentState);
        Assert.Equal(0, snapshot.CompletedUnitCount);
        Assert.Equal(0, snapshot.GoodUnitCount);
        Assert.Equal(0, snapshot.RejectedUnitCount);
        Assert.Empty(simulator.GetRecentEvents());
    }

    [Fact]
    public void ProductionCycleIncrementsCompletedAndGoodCountsAndAddsEvent()
    {
        using var simulator = CreateSimulator();

        simulator.SimulateProductionCycle();

        var snapshot = simulator.GetMachineSnapshot();
        var productionEvent = Assert.Single(simulator.GetRecentEvents());

        Assert.Equal(1, snapshot.CompletedUnitCount);
        Assert.Equal(1, snapshot.GoodUnitCount);
        Assert.Equal(0, snapshot.RejectedUnitCount);
        Assert.Equal(
            snapshot.CompletedUnitCount,
            snapshot.GoodUnitCount + snapshot.RejectedUnitCount);
        Assert.Equal(snapshot.MachineId, productionEvent.MachineId);
        Assert.Equal(ProductionEventType.UnitCompleted, productionEvent.EventType);
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
        Assert.All(events, productionEvent =>
            Assert.Equal(ProductionEventType.UnitCompleted, productionEvent.EventType));
    }

    private static MachineSimulator CreateSimulator() =>
        new(TimeProvider.System, NullLogger<MachineSimulator>.Instance);
}
