using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryPulse.OpcUa.Simulator;

public sealed class ConsoleControlService(
    IHostApplicationLifetime lifetime,
    ILogger<ConsoleControlService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var input = await Console.In.ReadLineAsync(stoppingToken);
            switch (input?.Trim().ToUpperInvariant())
            {
                case "F":
                    logger.LogInformation(
                        SimulatedMachine.Shared.InjectFault()
                            ? "Simulated OPC UA machine fault injected."
                            : "Machine is already faulted.");
                    break;
                case "C":
                    logger.LogInformation(
                        SimulatedMachine.Shared.ClearFault()
                            ? "Simulated OPC UA machine fault cleared."
                            : "Machine has no active fault.");
                    break;
                case "Q":
                    lifetime.StopApplication();
                    return;
            }
        }
    }
}
