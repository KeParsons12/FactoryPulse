namespace FactoryPulse.Web.Models;

public sealed record PersistenceHealthSnapshot(
    PersistenceHealthStatus Status,
    int PendingEventCount,
    int QueueCapacity,
    string? Message);
