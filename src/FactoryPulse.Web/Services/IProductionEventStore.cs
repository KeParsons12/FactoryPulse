using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public interface IProductionEventStore
{
    void Enqueue(ProductionEvent machineEvent);

    Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
        int count = 20,
        CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
