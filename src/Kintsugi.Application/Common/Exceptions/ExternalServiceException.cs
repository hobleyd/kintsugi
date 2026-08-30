namespace Kintsugi.Application.Common.Exceptions;

/// <summary>Thrown when a call to a third-party service (e.g. a local Ollama endpoint) fails.</summary>
public class ExternalServiceException : Exception
{
    public ExternalServiceException(string message) : base(message)
    {
    }

    public ExternalServiceException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
