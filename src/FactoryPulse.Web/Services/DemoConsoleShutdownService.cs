namespace FactoryPulse.Web.Services;

public sealed class DemoConsoleShutdownService(
    IHostApplicationLifetime lifetime,
    ILogger<DemoConsoleShutdownService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var input = await Console.In.ReadLineAsync(stoppingToken);
            if (input is null)
            {
                return;
            }

            if (string.Equals(input.Trim(), "Q", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Demo launcher requested graceful web application shutdown.");
                lifetime.StopApplication();
                return;
            }
        }
    }
}
