using FluentValidation;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;

public class UpdateAuthenticationSettingsCommandValidator : AbstractValidator<UpdateAuthenticationSettingsCommand>
{
    public UpdateAuthenticationSettingsCommandValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.ClientSecret).MaximumLength(512);
        RuleFor(x => x.HostedDomain).MaximumLength(255);

        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("A tenant ID is required for Microsoft Entra.")
            .MaximumLength(128)
            .When(x => x.Provider == AuthProvider.MicrosoftEntra);

        RuleFor(x => x.Authority)
            .NotEmpty()
            .WithMessage("An authority (issuer) URL is required for this provider.")
            .MaximumLength(512)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("Authority must be a valid absolute URL.")
            .When(x => x.Provider == AuthProvider.GenericOidc || x.Provider == AuthProvider.Clerk);
    }
}
