namespace FactoryPulse.Web.Services;

public sealed class MonitoringSession(TimeProvider timeProvider)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset StartedAtUtc { get; } = timeProvider.GetUtcNow();
}
