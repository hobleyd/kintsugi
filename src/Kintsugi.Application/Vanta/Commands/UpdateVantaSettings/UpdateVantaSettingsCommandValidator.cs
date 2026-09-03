using FluentValidation;

namespace Kintsugi.Application.Vanta.Commands.UpdateVantaSettings;

public class UpdateVantaSettingsCommandValidator : AbstractValidator<UpdateVantaSettingsCommand>
{
    private const string HttpsMessage = "Enter an absolute https:// URL.";

    public UpdateVantaSettingsCommandValidator()
    {
        // These duplicate invariants VantaSettings enforces too. Deliberately: the entity's version
        // is what keeps the invariant true no matter who writes to it, and this one is what turns a
        // bad save into a per-field message the form can put under the right box rather than a
        // single sentence at the top of the page.
        RuleFor(c => c.ApiBaseUrl)
            .Must(BeHttpsUrl).WithMessage(HttpsMessage)
            .When(c => !string.IsNullOrWhiteSpace(c.ApiBaseUrl));

        RuleFor(c => c.ConsoleBaseUrl)
            .Must(BeHttpsUrl).WithMessage(HttpsMessage)
            .When(c => !string.IsNullOrWhiteSpace(c.ConsoleBaseUrl));

        RuleFor(c => c.Severity)
            .InclusiveBetween(0d, 10d).WithMessage("Severity must be between 0 and 10.")
            .When(c => c.Severity is not null);

        RuleFor(c => c.SyncIntervalHours)
            .InclusiveBetween(1, 168).WithMessage("The sync interval must be between 1 and 168 hours.")
            .When(c => c.SyncIntervalHours is not null);

        RuleFor(c => c.ClientId)
            .NotEmpty().WithMessage("A client ID is required to enable the integration.")
            .When(c => c.Enabled);

        RuleFor(c => c.VulnerableComponentResourceId)
            .NotEmpty().WithMessage("The VulnerableComponent resource ID is required to enable the integration.")
            .When(c => c.Enabled);

        RuleFor(c => c.PackageVulnerabilityResourceId)
            .NotEmpty().WithMessage("The PackageVulnerabilityConnectors resource ID is required to enable the integration.")
            .When(c => c.Enabled);

        RuleFor(c => c.ConsoleBaseUrl)
            .NotEmpty().WithMessage("This server's address is required to enable the integration — Vanta links to it from every synced record.")
            .When(c => c.Enabled);
    }

    private static bool BeHttpsUrl(string? value) =>
        Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}
