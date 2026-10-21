using System.Threading.Channels;
using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FactoryPulse.Web.Services;

public sealed class ProductionEventStore : IProductionEventStore, IHostedService
{
    private readonly Channel<QueueItem> _queue;
    private readonly IDbContextFactory<FactoryPulseDbContext> _dbContextFactory;
    private readonly ILogger<ProductionEventStore> _logger;
    private readonly ProductionEventStoreOptions _options;
    private readonly Lock _healthLock = new();
    private PersistenceHealthStatus _healthStatus = PersistenceHealthStatus.Healthy;
    private string? _healthMessage;
    private int _pendingEventCount;
    private Task? _processingTask;

    public ProductionEventStore(
        IDbContextFactory<FactoryPulseDbContext> dbContextFactory,
        IOptions<ProductionEventStoreOptions> options,
        ILogger<ProductionEventStore> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _options = options.Value;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.QueueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumWriteAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.RetryDelay, TimeSpan.Zero);

        _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(
            _options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext =
                await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            SetHealth(
                PersistenceHealthStatus.Unavailable,
                "SQLite initialization failed. Restart the application after correcting the database problem.");
            _logger.LogCritical(exception, "SQLite production event store initialization failed.");
            throw;
        }

        _processingTask = ProcessQueueAsync();
        _logger.LogInformation(
            "SQLite production event store initialized with a bounded capacity of {QueueCapacity} events.",
            _options.QueueCapacity);
    }

    public bool TryEnqueue(ProductionEvent machineEvent)
    {
        ArgumentNullException.ThrowIfNull(machineEvent);

        var queueIsFull = false;
        var writerIsUnavailable = false;

        lock (_healthLock)
        {
            if (_healthStatus is PersistenceHealthStatus.Retrying or
                PersistenceHealthStatus.Unavailable)
            {
                return false;
            }

            if (!TryReservePendingEvent())
            {
                queueIsFull = true;
            }
            else if (_queue.Writer.TryWrite(new EventQueueItem(machineEvent)))
            {
                return true;
            }
            else
            {
                Interlocked.Decrement(ref _pendingEventCount);
                writerIsUnavailable = _processingTask?.IsCompleted == true;
                queueIsFull = !writerIsUnavailable;
            }

            if (writerIsUnavailable)
            {
                _healthStatus = PersistenceHealthStatus.Unavailable;
                _healthMessage =
                    "The SQLite writer is unavailable. Restart the application after correcting the database problem.";
            }
            else if (queueIsFull)
            {
                _healthStatus = PersistenceHealthStatus.Backpressured;
                _healthMessage =
                    $"The persistence queue reached its {_options.QueueCapacity}-event limit. Simulation activity is paused until it drains.";
            }
        }

        if (queueIsFull)
        {
            _logger.LogWarning(
                "Rejected {EventType} for {MachineId} because the persistence queue reached its {QueueCapacity}-event limit.",
                machineEvent.EventType,
                machineEvent.MachineId,
                _options.QueueCapacity);
        }

        return false;
    }

    public PersistenceHealthSnapshot GetHealthSnapshot()
    {
        lock (_healthLock)
        {
            return new PersistenceHealthSnapshot(
                _healthStatus,
                Volatile.Read(ref _pendingEventCount),
                _options.QueueCapacity,
                _healthMessage);
        }
    }

    public async Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        try
        {
            await using var dbContext =
                await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var storedEvents = await dbContext.ProductionEvents
                .AsNoTracking()
                .Where(machineEvent =>
                    machineEvent.EventType != ProductionEventType.MonitoringStarted &&
                    machineEvent.EventType != ProductionEventType.MonitoringStopped)
                .OrderByDescending(machineEvent => machineEvent.TimestampUtc)
                .ThenByDescending(machineEvent => machineEvent.Id)
                .Take(count)
                .ToListAsync(cancellationToken);

            return storedEvents.Select(machineEvent => machineEvent.ToModel()).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MarkUnavailable("Reading persisted event history failed.", exception);
            throw new PersistenceUnavailableException(
                "Persisted event history is unavailable.",
                exception);
        }
    }

    public async Task<ProductionEvent?> GetLatestFaultEventAsync(
        string machineId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        try
        {
            await using var dbContext =
                await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var storedEvent = await dbContext.ProductionEvents
                .AsNoTracking()
                .Where(machineEvent =>
                    machineEvent.MachineId == machineId &&
                    (machineEvent.EventType == ProductionEventType.FaultRaised ||
                     machineEvent.EventType == ProductionEventType.FaultCleared))
                .OrderByDescending(machineEvent => machineEvent.TimestampUtc)
                .ThenByDescending(machineEvent => machineEvent.Id)
                .FirstOrDefaultAsync(cancellationToken);

            return storedEvent?.ToModel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            MarkUnavailable("Reading persisted fault state failed.", exception);
            throw new PersistenceUnavailableException(
                "Persisted fault state is unavailable.",
                exception);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var processingTask = _processingTask
            ?? throw new PersistenceUnavailableException(
                "The production event store has not started.");
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _queue.Writer.WriteAsync(
                new FlushQueueItem(completion),
                cancellationToken);
            var completedTask = await Task.WhenAny(completion.Task, processingTask)
                .WaitAsync(cancellationToken);
            await completedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not PersistenceUnavailableException)
        {
            throw new PersistenceUnavailableException(
                "Queued production events could not be durably persisted.",
                exception);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();

        if (_processingTask is not null)
        {
            await _processingTask.WaitAsync(cancellationToken);
        }

        _logger.LogInformation("SQLite production event queue flushed.");
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync())
            {
                switch (item)
                {
                    case EventQueueItem eventItem:
                        await PersistWithRetryAsync(eventItem.Event);
                        Interlocked.Decrement(ref _pendingEventCount);
                        SetHealthyAfterWrite();
                        break;
                    case FlushQueueItem flushItem:
                        flushItem.Completion.TrySetResult();
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            _queue.Writer.TryComplete(exception);
            MarkUnavailable(
                $"SQLite writes failed after {_options.MaximumWriteAttempts} attempts. " +
                "Simulation activity is interlocked until the application is restarted.",
                exception);
            throw;
        }
    }

    private async Task PersistWithRetryAsync(ProductionEvent machineEvent)
    {
        for (var attempt = 1; attempt <= _options.MaximumWriteAttempts; attempt++)
        {
            try
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                dbContext.ProductionEvents.Add(PersistedProductionEvent.FromModel(machineEvent));
                await dbContext.SaveChangesAsync();
                return;
            }
            catch (Exception exception) when (attempt < _options.MaximumWriteAttempts)
            {
                SetHealth(
                    PersistenceHealthStatus.Retrying,
                    $"SQLite write attempt {attempt} failed. Simulation activity is paused while persistence retries.");
                _logger.LogError(
                    exception,
                    "Failed to persist {EventType} event for {MachineId} on attempt {Attempt}. Retrying in {RetryDelay}.",
                    machineEvent.EventType,
                    machineEvent.MachineId,
                    attempt,
                    _options.RetryDelay);
                await Task.Delay(_options.RetryDelay);
            }
        }
    }

    private void MarkUnavailable(string message, Exception exception)
    {
        SetHealth(PersistenceHealthStatus.Unavailable, message);
        _logger.LogCritical(
            exception,
            "{Message} {PendingEventCount} accepted event(s) are still not durable.",
            message,
            Volatile.Read(ref _pendingEventCount));
    }

    private bool TryReservePendingEvent()
    {
        while (true)
        {
            var currentCount = Volatile.Read(ref _pendingEventCount);
            if (currentCount >= _options.QueueCapacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                    ref _pendingEventCount,
                    currentCount + 1,
                    currentCount) == currentCount)
            {
                return true;
            }
        }
    }

    private void SetHealth(PersistenceHealthStatus status, string? message)
    {
        lock (_healthLock)
        {
            _healthStatus = status;
            _healthMessage = message;
        }
    }

    private void SetHealthyAfterWrite()
    {
        lock (_healthLock)
        {
            if (_healthStatus == PersistenceHealthStatus.Unavailable)
            {
                return;
            }

            _healthStatus = PersistenceHealthStatus.Healthy;
            _healthMessage = null;
        }
    }

    private abstract record QueueItem;

    private sealed record EventQueueItem(ProductionEvent Event) : QueueItem;

    private sealed record FlushQueueItem(TaskCompletionSource Completion) : QueueItem;
}
