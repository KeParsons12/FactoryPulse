namespace FactoryPulse.Web.Models;

public sealed record ProductionEvent(
    string MachineId,
    DateTimeOffset Timestamp,
    ProductionEventType EventType,
    string Details,
    TimeSpan? Duration = null,
    long EventId = 0);
