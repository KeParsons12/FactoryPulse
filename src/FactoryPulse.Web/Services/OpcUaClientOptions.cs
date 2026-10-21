namespace FactoryPulse.Web.Services;

public sealed class OpcUaClientOptions
{
    public const string ConfigurationSection = "FactoryPulse:OpcUa";

    public string Endpoint { get; set; } =
        "opc.tcp://localhost:62560/FactoryPulse";

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(15);

    public int MaximumSessionReconnectAttempts { get; set; } = 5;
}
