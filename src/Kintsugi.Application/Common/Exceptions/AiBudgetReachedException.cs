namespace Kintsugi.Application.Common.Exceptions;

/// <summary>
/// Thrown instead of making an AI call once the month's spending limit is reached. A kind of
/// <see cref="ExternalServiceException"/> so every caller that already reports a failed AI call
/// reports this one too, with the message below, rather than needing to know about budgets.
/// </summary>
public class AiBudgetReachedException : ExternalServiceException
{
    public AiBudgetReachedException(string message) : base(message)
    {
    }
}
