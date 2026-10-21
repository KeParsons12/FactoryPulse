namespace FactoryPulse.Web.Services;

public sealed class ProductionEventStoreOptions
{
    public const string ConfigurationSection = "FactoryPulse:EventPersistence";

    public int QueueCapacity { get; set; } = 100;

    public int MaximumWriteAttempts { get; set; } = 3;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
