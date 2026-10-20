using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public sealed class MachineSimulator(
    TimeProvider timeProvider,
    ILogger<MachineSimulator> logger) : BackgroundService, IMachineSimulation
{
    internal const int MaximumRecentEventCount = 20;
    internal static readonly TimeSpan UnitInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _stateLock = new();
    private readonly Queue<ProductionEvent> _recentEvents = new();
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<MachineSimulator> _logger = logger;
    private long _completedUnitCount;
    private long _goodUnitCount;

    public MachineSnapshot GetMachineSnapshot()
    {
        lock (_stateLock)
        {
            return new MachineSnapshot(
                MachineId: "machine-01",
                MachineName: "Machine 01",
                CurrentState: MachineState.Running,
                CompletedUnitCount: _completedUnitCount,
                GoodUnitCount: _goodUnitCount,
                RejectedUnitCount: 0);
        }
    }

    public IReadOnlyList<ProductionEvent> GetRecentEvents()
    {
        lock (_stateLock)
        {
            return _recentEvents.Reverse().ToArray();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Machine 01 simulation started.");
        using var timer = new PeriodicTimer(UnitInterval, _timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SimulateProductionCycle();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during an orderly application shutdown.
        }

        _logger.LogInformation("Machine 01 simulation stopped.");
    }

    internal void SimulateProductionCycle()
    {
        lock (_stateLock)
        {
            _completedUnitCount++;
            _goodUnitCount++;
            _recentEvents.Enqueue(new ProductionEvent(
                MachineId: "machine-01",
                Timestamp: _timeProvider.GetUtcNow(),
                EventType: ProductionEventType.UnitCompleted));

            while (_recentEvents.Count > MaximumRecentEventCount)
            {
                _recentEvents.Dequeue();
            }
        }
    }
}
