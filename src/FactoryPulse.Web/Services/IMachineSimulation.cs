using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public interface IMachineSimulation
{
    MachineSnapshot GetMachineSnapshot();

    IReadOnlyList<ProductionEvent> GetRecentEvents();
}
