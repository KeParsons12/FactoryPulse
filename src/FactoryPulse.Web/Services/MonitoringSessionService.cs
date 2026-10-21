using FactoryPulse.Web.Models;

namespace FactoryPulse.Web.Services;

public sealed class MonitoringSessionService(
    MonitoringSession session,
    TimeProvider timeProvider,
    IProductionEventStore eventStore,
    ILogger<MonitoringSessionService> logger) : IHostedService
{
    private const string MachineId = "machine-01";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!eventStore.TryEnqueue(new ProductionEvent(
                MachineId,
                session.StartedAtUtc,
                ProductionEventType.MonitoringStarted,
                session.Id)))
        {
            throw new PersistenceUnavailableException(
                "The monitoring session start could not be queued.");
        }

        await eventStore.FlushAsync(cancellationToken);
        logger.LogInformation(
            "Monitoring session {SessionId} started at {StartedAtUtc}.",
            session.Id,
            session.StartedAtUtc);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var stoppedAtUtc = timeProvider.GetUtcNow();
        if (!eventStore.TryEnqueue(new ProductionEvent(
                MachineId,
                stoppedAtUtc,
                ProductionEventType.MonitoringStopped,
                session.Id)))
        {
            logger.LogError(
                "Monitoring session {SessionId} stop could not be queued.",
                session.Id);
            return;
        }

        await eventStore.FlushAsync(cancellationToken);
        logger.LogInformation(
            "Monitoring session {SessionId} stopped at {StoppedAtUtc}.",
            session.Id,
            stoppedAtUtc);
    }
}
