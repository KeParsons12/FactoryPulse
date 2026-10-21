namespace FactoryPulse.Web.Models;

public sealed record ActiveFaultSnapshot(
    string Code,
    string Description,
    DateTimeOffset StartedAtUtc,
    TimeSpan ElapsedDuration);
