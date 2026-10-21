namespace FactoryPulse.Web.Models;

public sealed record ProductionAnalyticsSnapshot(
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    long TotalProduction,
    double ProductionRatePerHour,
    int FaultCount,
    TimeSpan TotalFaultDowntime,
    double? AvailabilityPercent,
    string? AvailabilityUnavailableReason,
    TimeSpan? MeanTimeToRepair,
    string? MeanTimeToRepairUnavailableReason,
    TimeSpan? MeanTimeBetweenFailures,
    string? MeanTimeBetweenFailuresUnavailableReason,
    TimeSpan ConfirmedMonitoringDuration,
    bool HasCompleteMonitoringCoverage,
    IReadOnlyList<ProductionBucket> ProductionBuckets);

public sealed record ProductionBucket(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long UnitCount);
