using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public interface IOpcUaTelemetry
{
    OpcUaTelemetrySnapshot GetSnapshot();
}
