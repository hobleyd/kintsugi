namespace Kintsugi.Application.Common.Exceptions;

/// <summary>Thrown when a request is well-formed but not authorized to do what it's asking —
/// an invalid/missing enrollment token, or a CSR/certificate that doesn't prove what it claims.</summary>
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message)
    {
    }
}
