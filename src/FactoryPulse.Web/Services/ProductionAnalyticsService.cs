using FactoryPulse.Web.Data;
using FactoryPulse.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace FactoryPulse.Web.Services;

public sealed class ProductionAnalyticsService(
    IDbContextFactory<FactoryPulseDbContext> dbContextFactory,
    TimeProvider timeProvider,
    MonitoringSession monitoringSession,
    ILogger<ProductionAnalyticsService> logger) : IProductionAnalyticsService
{
    public async Task<ProductionAnalyticsSnapshot> GetAnalyticsAsync(
        string machineId,
        AnalyticsPeriod period,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAnalyticsCoreAsync(
                machineId,
                period,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reading persisted production analytics failed.");
            throw new PersistenceUnavailableException(
                "Persisted production analytics are unavailable.",
                exception);
        }
    }

    private async Task<ProductionAnalyticsSnapshot> GetAnalyticsCoreAsync(
        string machineId,
        AnalyticsPeriod period,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        var periodEnd = timeProvider.GetUtcNow();
        var periodStart = periodEnd - period.GetDuration();
        var bucketCount = period.GetBucketCount();

        await using var dbContext =
            await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var periodStartUtc = periodStart.UtcDateTime;
        var periodEndUtc = periodEnd.UtcDateTime;
        var totalProduction = await dbContext.ProductionEvents
            .AsNoTracking()
            .CountAsync(machineEvent =>
                machineEvent.MachineId == machineId &&
                machineEvent.EventType == ProductionEventType.UnitCompleted &&
                machineEvent.TimestampUtc >= periodStartUtc &&
                machineEvent.TimestampUtc < periodEndUtc,
                cancellationToken);
        var productionBuckets = await GetProductionBucketsAsync(
            dbContext,
            machineId,
            periodStart,
            periodEnd,
            bucketCount,
            cancellationToken);
        if (productionBuckets.Sum(bucket => bucket.UnitCount) != totalProduction)
        {
            throw new InvalidOperationException(
                "Production bucket counts did not match the exact-range production total.");
        }

        var faultEvents = await GetBoundaryAwareEventsAsync(
            dbContext,
            machineId,
            periodStart,
            periodEnd,
            [ProductionEventType.FaultRaised, ProductionEventType.FaultCleared],
            cancellationToken);
        var faultMetrics = CalculateFaultMetrics(
            faultEvents,
            periodStart,
            periodEnd);

        var monitoringEvents = await GetBoundaryAwareEventsAsync(
            dbContext,
            machineId,
            periodStart,
            periodEnd,
            [ProductionEventType.MonitoringStarted, ProductionEventType.MonitoringStopped],
            cancellationToken);
        var monitoring = CalculateMonitoringCoverage(
            monitoringEvents,
            monitoringSession.Id,
            periodStart,
            periodEnd);

        var periodDuration = periodEnd - periodStart;
        var productionRate = totalProduction / periodDuration.TotalHours;
        var completeCoverage = monitoring.CoversEntirePeriod;
        double? faultFreeMonitoringPercent = completeCoverage
            ? 100 * Math.Max(0, periodDuration.TotalSeconds - faultMetrics.Downtime.TotalSeconds) /
                periodDuration.TotalSeconds
            : null;
        var faultFreeMonitoringReason = completeCoverage
            ? null
            : "N/A because the selected period includes time when monitoring was offline or its state is unknown.";

        TimeSpan? mttr = faultMetrics.CompletedDurations.Count > 0
            ? TimeSpan.FromTicks((long)faultMetrics.CompletedDurations.Average(
                duration => duration.Ticks))
            : null;
        var mttrReason = mttr is null
            ? "N/A because no fault was cleared during the selected period."
            : null;

        TimeSpan? meanFaultFreeTimePerFault = null;
        string? meanFaultFreeTimeReason;
        if (!completeCoverage)
        {
            meanFaultFreeTimeReason =
                "N/A because the selected period includes time when monitoring was offline or its state is unknown.";
        }
        else if (faultMetrics.FaultCount == 0)
        {
            meanFaultFreeTimeReason = "N/A because no faults occurred during the selected period.";
        }
        else
        {
            var confirmedUptime = periodDuration - faultMetrics.Downtime;
            meanFaultFreeTimePerFault = TimeSpan.FromTicks(Math.Max(0, confirmedUptime.Ticks) /
                faultMetrics.FaultCount);
            meanFaultFreeTimeReason = null;
        }

        return new ProductionAnalyticsSnapshot(
            periodStart,
            periodEnd,
            totalProduction,
            productionRate,
            faultMetrics.FaultCount,
            faultMetrics.Downtime,
            faultFreeMonitoringPercent,
            faultFreeMonitoringReason,
            mttr,
            mttrReason,
            meanFaultFreeTimePerFault,
            meanFaultFreeTimeReason,
            monitoring.ConfirmedDuration,
            completeCoverage,
            productionBuckets);
    }

    private static async Task<IReadOnlyList<ProductionBucket>> GetProductionBucketsAsync(
        FactoryPulseDbContext dbContext,
        string machineId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        int bucketCount,
        CancellationToken cancellationToken)
    {
        var bucketDuration = (periodEnd - periodStart) / bucketCount;
        var buckets = new List<ProductionBucket>(bucketCount);

        for (var index = 0; index < bucketCount; index++)
        {
            var bucketStart = periodStart + bucketDuration * index;
            var bucketEnd = index == bucketCount - 1
                ? periodEnd
                : periodStart + bucketDuration * (index + 1);
            var bucketStartUtc = bucketStart.UtcDateTime;
            var bucketEndUtc = bucketEnd.UtcDateTime;
            var unitCount = await dbContext.ProductionEvents
                .AsNoTracking()
                .CountAsync(machineEvent =>
                    machineEvent.MachineId == machineId &&
                    machineEvent.EventType == ProductionEventType.UnitCompleted &&
                    machineEvent.TimestampUtc >= bucketStartUtc &&
                    machineEvent.TimestampUtc < bucketEndUtc,
                    cancellationToken);

            buckets.Add(new ProductionBucket(bucketStart, bucketEnd, unitCount));
        }

        return buckets;
    }

    private static async Task<IReadOnlyList<ProductionEvent>> GetBoundaryAwareEventsAsync(
        FactoryPulseDbContext dbContext,
        string machineId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        ProductionEventType[] eventTypes,
        CancellationToken cancellationToken)
    {
        var startUtc = periodStart.UtcDateTime;
        var endUtc = periodEnd.UtcDateTime;

        var preceding = await dbContext.ProductionEvents
            .AsNoTracking()
            .Where(machineEvent =>
                machineEvent.MachineId == machineId &&
                eventTypes.Contains(machineEvent.EventType) &&
                machineEvent.TimestampUtc < startUtc)
            .OrderByDescending(machineEvent => machineEvent.TimestampUtc)
            .ThenByDescending(machineEvent => machineEvent.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var inPeriod = await dbContext.ProductionEvents
            .AsNoTracking()
            .Where(machineEvent =>
                machineEvent.MachineId == machineId &&
                eventTypes.Contains(machineEvent.EventType) &&
                machineEvent.TimestampUtc >= startUtc &&
                machineEvent.TimestampUtc <= endUtc)
            .OrderBy(machineEvent => machineEvent.TimestampUtc)
            .ThenBy(machineEvent => machineEvent.Id)
            .ToListAsync(cancellationToken);

        return (preceding is null ? inPeriod : inPeriod.Prepend(preceding))
            .Select(machineEvent => machineEvent.ToModel())
            .ToArray();
    }

    private static FaultMetrics CalculateFaultMetrics(
        IReadOnlyList<ProductionEvent> events,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        DateTimeOffset? activeFaultStart = null;
        var downtime = TimeSpan.Zero;
        var faultCount = 0;
        List<TimeSpan> completedDurations = [];

        foreach (var machineEvent in events)
        {
            if (machineEvent.EventType == ProductionEventType.FaultRaised)
            {
                activeFaultStart = machineEvent.Timestamp;
                if (machineEvent.Timestamp >= periodStart && machineEvent.Timestamp < periodEnd)
                {
                    faultCount++;
                }
            }
            else if (machineEvent.EventType == ProductionEventType.FaultCleared)
            {
                var startedAt = activeFaultStart ??
                    (machineEvent.Duration is { } duration
                        ? machineEvent.Timestamp - duration
                        : (DateTimeOffset?)null);
                if (startedAt is { } start)
                {
                    downtime += GetOverlap(start, machineEvent.Timestamp, periodStart, periodEnd);
                }

                if (machineEvent.Timestamp >= periodStart &&
                    machineEvent.Timestamp < periodEnd &&
                    machineEvent.Duration is { } completedDuration)
                {
                    completedDurations.Add(completedDuration);
                }

                activeFaultStart = null;
            }
        }

        if (activeFaultStart is { } activeStart)
        {
            downtime += GetOverlap(activeStart, periodEnd, periodStart, periodEnd);
        }

        return new FaultMetrics(faultCount, downtime, completedDurations);
    }

    private static MonitoringCoverage CalculateMonitoringCoverage(
        IReadOnlyList<ProductionEvent> events,
        string currentSessionId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        ProductionEvent? openSession = null;

        foreach (var machineEvent in events)
        {
            if (machineEvent.EventType == ProductionEventType.MonitoringStarted)
            {
                openSession = machineEvent;
            }
            else if (machineEvent.EventType == ProductionEventType.MonitoringStopped &&
                     openSession is not null &&
                     machineEvent.Details == openSession.Details)
            {
                intervals.Add((openSession.Timestamp, machineEvent.Timestamp));
                openSession = null;
            }
        }

        if (openSession is not null && openSession.Details == currentSessionId)
        {
            intervals.Add((openSession.Timestamp, periodEnd));
        }

        var clipped = intervals
            .Select(interval => (
                Start: interval.Start < periodStart ? periodStart : interval.Start,
                End: interval.End > periodEnd ? periodEnd : interval.End))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ToArray();
        var confirmedDuration = clipped.Aggregate(
            TimeSpan.Zero,
            (total, interval) => total + (interval.End - interval.Start));

        var cursor = periodStart;
        foreach (var interval in clipped)
        {
            if (interval.Start > cursor)
            {
                break;
            }

            if (interval.End > cursor)
            {
                cursor = interval.End;
            }
        }

        return new MonitoringCoverage(
            confirmedDuration,
            cursor >= periodEnd);
    }

    private static TimeSpan GetOverlap(
        DateTimeOffset intervalStart,
        DateTimeOffset intervalEnd,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        var start = intervalStart < periodStart ? periodStart : intervalStart;
        var end = intervalEnd > periodEnd ? periodEnd : intervalEnd;
        return end > start ? end - start : TimeSpan.Zero;
    }

    private sealed record FaultMetrics(
        int FaultCount,
        TimeSpan Downtime,
        IReadOnlyList<TimeSpan> CompletedDurations);

    private sealed record MonitoringCoverage(
        TimeSpan ConfirmedDuration,
        bool CoversEntirePeriod);

}
