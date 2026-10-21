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
    private MachineState _currentState = MachineState.Idle;
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

            if (!TryAddEventLocked(
                    ProductionEventType.MachineStarted,
                    "Machine started"))
            {
                return false;
            }

            _currentState = MachineState.Running;
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

            if (!TryAddEventLocked(
                    ProductionEventType.MachineStopped,
                    "Machine stopped"))
            {
                return false;
            }

            _currentState = MachineState.Idle;
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
            if (!TryAddEventLocked(
                    ProductionEventType.FaultRaised,
                    $"{SimulatedFaultCode} — {SimulatedFaultDescription}",
                    timestamp: startedAtUtc))
            {
                return false;
            }

            _activeFault = new ActiveFault(
                SimulatedFaultCode,
                SimulatedFaultDescription,
                startedAtUtc);
            _currentState = MachineState.Faulted;
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

            if (!TryAddEventLocked(
                    ProductionEventType.FaultCleared,
                    details,
                    duration,
                    clearedAtUtc))
            {
                return false;
            }

            _activeFault = null;
            _currentState = MachineState.Idle;
            return true;
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RestorePersistentStateAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
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

            if (!TryAddEventLocked(
                    ProductionEventType.UnitCompleted,
                    "Good unit completed"))
            {
                return false;
            }

            _completedUnitCount++;
            _goodUnitCount++;
            return true;
        }
    }

    internal async Task RestorePersistentStateAsync(CancellationToken cancellationToken = default)
    {
        var latestFaultEvent = await _eventStore.GetLatestFaultEventAsync(
            MachineId,
            cancellationToken);

        if (latestFaultEvent?.EventType != ProductionEventType.FaultRaised)
        {
            return;
        }

        lock (_stateLock)
        {
            _activeFault = new ActiveFault(
                SimulatedFaultCode,
                SimulatedFaultDescription,
                latestFaultEvent.Timestamp);
            _currentState = MachineState.Faulted;
        }

        _logger.LogWarning(
            "Restored uncleared fault {FaultCode} for {MachineId}, raised at {StartedAtUtc}.",
            SimulatedFaultCode,
            MachineId,
            latestFaultEvent.Timestamp);
    }

    private bool TryAddEventLocked(
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

        if (!_eventStore.TryEnqueue(machineEvent))
        {
            return false;
        }

        _recentEvents.Enqueue(machineEvent);

        while (_recentEvents.Count > MaximumRecentEventCount)
        {
            _recentEvents.Dequeue();
        }

        return true;
    }

    private sealed record ActiveFault(
        string Code,
        string Description,
        DateTimeOffset StartedAtUtc);
}
