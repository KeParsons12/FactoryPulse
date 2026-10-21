namespace FactoryPulse.Web.Models;

public enum PersistenceHealthStatus
{
    Healthy,
    Retrying,
    Backpressured,
    Unavailable
}
