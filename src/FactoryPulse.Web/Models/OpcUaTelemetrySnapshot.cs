namespace FactoryPulse.Web.Models;

public sealed record OpcUaTelemetrySnapshot(
    OpcUaConnectionState ConnectionState,
    string Endpoint,
    string? MachineState,
    uint? GoodCount,
    uint? RejectCount,
    uint? TotalCount,
    string? FaultCode,
    uint? Heartbeat,
    DateTimeOffset? LastGoodDataTimestamp,
    bool IsStale,
    bool HasValidData,
    string? Warning);
