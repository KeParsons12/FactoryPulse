using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public interface IProductionEventStore
{
    bool TryEnqueue(ProductionEvent machineEvent);

    PersistenceHealthSnapshot GetHealthSnapshot();

    Task<IReadOnlyList<ProductionEvent>> GetRecentEventsAsync(
        int count = 20,
        CancellationToken cancellationToken = default);

    Task<ProductionEvent?> GetLatestFaultEventAsync(
        string machineId,
        CancellationToken cancellationToken = default);

    Task FlushAsync(CancellationToken cancellationToken = default);
}
