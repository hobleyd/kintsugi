using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.EnrollAgent;

public class EnrollAgentCommandValidator : AbstractValidator<EnrollAgentCommand>
{
    public EnrollAgentCommandValidator()
    {
        // This becomes the issued certificate's Subject CN (see CaService), built by simple string
        // interpolation rather than a DN builder — restricting the character set here rules out
        // any possibility of injecting extra RDNs via a crafted serial number.
        //
        // '/' is allowed because real hardware ships with it (e.g. Win32_BIOS "B709098/002") and it
        // is not a special character in an RFC 4514 DN, so it cannot start a new RDN. It must not
        // lead, trail, repeat, sit next to a '.', or share a serial with '..', which keeps anything
        // path-like ("../", "/etc", "a//b") out even though no code currently uses the serial as a
        // path.
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128)
            .Matches("^[A-Za-z0-9._-]+(/[A-Za-z0-9_-][A-Za-z0-9._-]*)*$")
            .Must(s => s is null || !s.Contains('/') || (!s.Contains("./") && !s.Contains("/.") && !s.Contains("..")))
            .WithMessage("SerialNumber may only contain letters, digits, '.', '_', '-' and single '/' separators.");
        RuleFor(x => x.EnrollmentToken).NotEmpty();
        RuleFor(x => x.CsrPem).NotEmpty().Must(pem => pem.Contains("BEGIN CERTIFICATE REQUEST"))
            .WithMessage("CsrPem must be a PEM-encoded PKCS#10 certificate signing request.");
    }
}
