using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public interface IProductionAnalyticsService
{
    Task<ProductionAnalyticsSnapshot> GetAnalyticsAsync(
        string machineId,
        AnalyticsPeriod period,
        CancellationToken cancellationToken = default);
}
