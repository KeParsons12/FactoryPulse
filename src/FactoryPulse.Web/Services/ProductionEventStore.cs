using System.Threading.Channels;
using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace FactoryPulse.Web.Services;

public sealed class ProductionEventStore(
    IDbContextFactory<FactoryPulseDbContext> dbContextFactory,
    ILogger<ProductionEventStore> logger) : IProductionEventStore, IHostedService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private const int MaximumWriteAttempts = 3;

    private readonly Channel<QueueItem> _queue = Channel.CreateUnbounded<QueueItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly IDbContextFactory<FactoryPulseDbContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<ProductionEventStore> _logger = logger;
    private Task? _processingTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);

        _processingTask = ProcessQueueAsync();
        _logger.LogInformation("SQLite production event store initialized.");
    }

    public void Enqueue(ProductionEvent machineEvent)
    {
        ArgumentNullException.ThrowIfNull(machineEvent);

        if (!_queue.Writer.TryWrite(new EventQueueItem(machineEvent)))
        {
            throw new InvalidOperationException(
                "The production event queue is closed; the event could not be accepted.");
        }
    }

    public async Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
        int count = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var storedEvents = await dbContext.ProductionEvents
            .AsNoTracking()
            .OrderByDescending(machineEvent => machineEvent.TimestampUtc)
            .ThenByDescending(machineEvent => machineEvent.Id)
            .Take(count)
            .ToListAsync(cancellationToken);

        return storedEvents.Select(machineEvent => machineEvent.ToModel()).ToArray();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_queue.Writer.TryWrite(new FlushQueueItem(completion)))
        {
            throw new InvalidOperationException("The production event queue is closed.");
        }

        var processingTask = _processingTask
            ?? throw new InvalidOperationException("The production event store has not started.");
        var completedTask = await Task.WhenAny(completion.Task, processingTask)
            .WaitAsync(cancellationToken);
        await completedTask;
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
            _logger.LogCritical(exception, "The SQLite production event queue stopped.");
            throw;
        }
    }

    private async Task PersistWithRetryAsync(ProductionEvent machineEvent)
    {
        for (var attempt = 1; attempt <= MaximumWriteAttempts; attempt++)
        {
            try
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
                dbContext.ProductionEvents.Add(PersistedProductionEvent.FromModel(machineEvent));
                await dbContext.SaveChangesAsync();
                return;
            }
            catch (Exception exception) when (attempt < MaximumWriteAttempts)
            {
                _logger.LogError(
                    exception,
                    "Failed to persist {EventType} event for {MachineId} on attempt {Attempt}. Retrying in {RetryDelay}.",
                    machineEvent.EventType,
                    machineEvent.MachineId,
                    attempt,
                    RetryDelay);
                await Task.Delay(RetryDelay);
            }
        }

        throw new InvalidOperationException(
            $"Failed to persist {machineEvent.EventType} event for {machineEvent.MachineId} " +
            $"after {MaximumWriteAttempts} attempts.");
    }

    private abstract record QueueItem;

    private sealed record EventQueueItem(ProductionEvent Event) : QueueItem;

    private sealed record FlushQueueItem(TaskCompletionSource Completion) : QueueItem;
}
