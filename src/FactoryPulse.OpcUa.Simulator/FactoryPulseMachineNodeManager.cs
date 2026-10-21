using FactoryPulse.OpcUa.Simulator;
using Opc.Ua.Server.Fluent;

namespace FactoryPulseMachine;

[NodeManager(NamespaceUri = "urn:factorypulse:opcua:machine")]
public partial class FactoryPulseMachineNodeManager
{
    partial void Configure(IFactoryPulseMachineNodeManagerBuilder builder)
    {
        builder.Machine01.State.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().State);
        builder.Machine01.GoodCount.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().GoodCount);
        builder.Machine01.RejectCount.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().RejectCount);
        builder.Machine01.TotalCount.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().TotalCount);
        builder.Machine01.FaultCode.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().FaultCode);
        builder.Machine01.Heartbeat.PollEvery(
            TimeSpan.FromMilliseconds(500),
            () => SimulatedMachine.Shared.GetSnapshot().Heartbeat);
    }
}
