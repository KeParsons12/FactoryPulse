namespace FactoryPulse.Web.Models;

public sealed record MachineSnapshot(
    string MachineId,
    string MachineName,
    MachineState CurrentState,
    long CompletedUnitCount,
    long GoodUnitCount,
    long RejectedUnitCount,
    ActiveFaultSnapshot? ActiveFault);
