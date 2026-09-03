namespace Kintsugi.Application.Common.Exceptions;

/// <summary>Thrown when a request is well-formed and authorized but collides with something already
/// stored — a uniquely-indexed column another row already owns. Distinct from
/// <see cref="NotFoundException"/> and <see cref="DomainException"/> because the caller is not at
/// fault and nothing about the request is invalid: the same request would have succeeded a moment
/// earlier, or against a different server.
///
/// It exists because the alternative is what an unhandled unique-index violation actually produces:
/// a bare 500 whose only clue is a Postgres constraint name in the server log, on a route an agent
/// calls unattended every hour. See CreateHostCommandHandler, where a hostname already held by a
/// live host is the collision that matters.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message) : base(message)
    {
    }
}
