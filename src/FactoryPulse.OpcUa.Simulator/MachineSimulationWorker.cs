using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryPulse.OpcUa.Simulator;

public sealed class MachineSimulationWorker(
    ILogger<MachineSimulationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "External machine simulation is Running. Press F to inject a fault, C to clear it, or Q to stop.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                SimulatedMachine.Shared.Advance();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during graceful shutdown.
        }
    }
}
