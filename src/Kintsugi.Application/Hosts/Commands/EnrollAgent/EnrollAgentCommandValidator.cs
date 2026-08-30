using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.EnrollAgent;

public class EnrollAgentCommandValidator : AbstractValidator<EnrollAgentCommand>
{
    public EnrollAgentCommandValidator()
    {
        // This becomes the issued certificate's Subject CN (see CaService), built by simple string
        // interpolation rather than a DN builder — restricting the character set here rules out
        // any possibility of injecting extra RDNs via a crafted serial number.
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128)
            .Matches("^[A-Za-z0-9._-]+$").WithMessage("SerialNumber may only contain letters, digits, '.', '_', and '-'.");
        RuleFor(x => x.EnrollmentToken).NotEmpty();
        RuleFor(x => x.CsrPem).NotEmpty().Must(pem => pem.Contains("BEGIN CERTIFICATE REQUEST"))
            .WithMessage("CsrPem must be a PEM-encoded PKCS#10 certificate signing request.");
    }
}
