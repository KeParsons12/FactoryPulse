namespace FactoryPulse.Web.Models;

public enum AnalyticsPeriod
{
    LastHour,
    Last24Hours,
    Last7Days
}

public static class AnalyticsPeriodExtensions
{
    public static TimeSpan GetDuration(this AnalyticsPeriod period) => period switch
    {
        AnalyticsPeriod.LastHour => TimeSpan.FromHours(1),
        AnalyticsPeriod.Last24Hours => TimeSpan.FromHours(24),
        AnalyticsPeriod.Last7Days => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, null)
    };

    public static int GetBucketCount(this AnalyticsPeriod period) => period switch
    {
        AnalyticsPeriod.LastHour => 12,
        AnalyticsPeriod.Last24Hours => 12,
        AnalyticsPeriod.Last7Days => 14,
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, null)
    };
}
