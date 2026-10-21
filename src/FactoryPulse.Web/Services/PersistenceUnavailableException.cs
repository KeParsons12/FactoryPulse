namespace FactoryPulse.Web.Services;

public sealed class PersistenceUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
