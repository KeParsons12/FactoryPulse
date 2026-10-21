using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public sealed class MachineSimulator(
    TimeProvider timeProvider,
    IProductionEventStore eventStore,
    ILogger<MachineSimulator> logger) : BackgroundService, IMachineSimulation
{
    internal const int MaximumRecentEventCount = 20;
    internal const string SimulatedFaultCode = "SIM-101";
    internal const string SimulatedFaultDescription = "Simulated sensor fault";
    internal static readonly TimeSpan UnitInterval = TimeSpan.FromSeconds(2);

    private const string MachineId = "machine-01";
    private const string MachineName = "Machine 01";

    private readonly Lock _stateLock = new();
    private readonly Queue<ProductionEvent> _recentEvents = new();
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IProductionEventStore _eventStore = eventStore;
    private readonly ILogger<MachineSimulator> _logger = logger;
    private MachineState _currentState = MachineState.Running;
    private long _completedUnitCount;
    private long _goodUnitCount;
    private ActiveFault? _activeFault;

    public MachineSnapshot GetMachineSnapshot()
    {
        lock (_stateLock)
        {
            ActiveFaultSnapshot? faultSnapshot = null;

            if (_activeFault is not null)
            {
                var now = _timeProvider.GetUtcNow();
                var elapsed = now >= _activeFault.StartedAtUtc
                    ? now - _activeFault.StartedAtUtc
                    : TimeSpan.Zero;

                faultSnapshot = new ActiveFaultSnapshot(
                    _activeFault.Code,
                    _activeFault.Description,
                    _activeFault.StartedAtUtc,
                    elapsed);
            }

            return new MachineSnapshot(
                MachineId,
                MachineName,
                _currentState,
                _completedUnitCount,
                _goodUnitCount,
                RejectedUnitCount: 0,
                faultSnapshot);
        }
    }

    public IReadOnlyList<ProductionEvent> GetRecentEvents()
    {
        lock (_stateLock)
        {
            return _recentEvents.Reverse().ToArray();
        }
    }

    public bool TryStart()
    {
        lock (_stateLock)
        {
            if (_currentState != MachineState.Idle)
            {
                return false;
            }

            _currentState = MachineState.Running;
            AddEventLocked(ProductionEventType.MachineStarted, "Machine started");
            return true;
        }
    }

    public bool TryStop()
    {
        lock (_stateLock)
        {
            if (_currentState != MachineState.Running)
            {
                return false;
            }

            _currentState = MachineState.Idle;
            AddEventLocked(ProductionEventType.MachineStopped, "Machine stopped");
            return true;
        }
    }

    public bool TryInjectFault()
    {
        lock (_stateLock)
        {
            if (_currentState is not (MachineState.Running or MachineState.Idle))
            {
                return false;
            }

            var startedAtUtc = _timeProvider.GetUtcNow();
            _activeFault = new ActiveFault(
                SimulatedFaultCode,
                SimulatedFaultDescription,
                startedAtUtc);
            _currentState = MachineState.Faulted;
            AddEventLocked(
                ProductionEventType.FaultRaised,
                $"{SimulatedFaultCode} — {SimulatedFaultDescription}",
                timestamp: startedAtUtc);
            return true;
        }
    }

    public bool TryClearFault()
    {
        lock (_stateLock)
        {
            if (_currentState != MachineState.Faulted || _activeFault is null)
            {
                return false;
            }

            var clearedAtUtc = _timeProvider.GetUtcNow();
            var duration = clearedAtUtc >= _activeFault.StartedAtUtc
                ? clearedAtUtc - _activeFault.StartedAtUtc
                : TimeSpan.Zero;
            var details = $"{_activeFault.Code} — {_activeFault.Description}";

            _activeFault = null;
            _currentState = MachineState.Idle;
            AddEventLocked(
                ProductionEventType.FaultCleared,
                details,
                duration,
                clearedAtUtc);
            return true;
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

    internal bool SimulateProductionCycle()
    {
        lock (_stateLock)
        {
            if (_currentState != MachineState.Running)
            {
                return false;
            }

            _completedUnitCount++;
            _goodUnitCount++;
            AddEventLocked(
                ProductionEventType.UnitCompleted,
                "Good unit completed");
            return true;
        }
    }

    private void AddEventLocked(
        ProductionEventType eventType,
        string details,
        TimeSpan? duration = null,
        DateTimeOffset? timestamp = null)
    {
        var machineEvent = new ProductionEvent(
            MachineId,
            timestamp ?? _timeProvider.GetUtcNow(),
            eventType,
            details,
            duration);

        _recentEvents.Enqueue(machineEvent);
        _eventStore.Enqueue(machineEvent);

        while (_recentEvents.Count > MaximumRecentEventCount)
        {
            _recentEvents.Dequeue();
        }
    }

    private sealed record ActiveFault(
        string Code,
        string Description,
        DateTimeOffset StartedAtUtc);
}
