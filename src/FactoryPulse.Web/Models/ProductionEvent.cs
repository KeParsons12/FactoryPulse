namespace FactoryPulse.Web.Models;

public sealed record ProductionEvent(
    string MachineId,
    DateTimeOffset Timestamp,
    ProductionEventType EventType);
