using System.Text.RegularExpressions;
using FluentValidation;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;

public partial class UpdateAuthenticationSettingsCommandValidator : AbstractValidator<UpdateAuthenticationSettingsCommand>
{
    public UpdateAuthenticationSettingsCommandValidator()
    {
        RuleFor(x => x.Provider).IsInEnum();
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(256);
        RuleFor(x => x.ClientSecret).MaximumLength(512);
        RuleFor(x => x.HostedDomain).MaximumLength(255);

        RuleFor(x => x.AdminEmails!.Count)
            .LessThanOrEqualTo(MaxAdminEmails)
            .WithMessage($"The administrators list can hold at most {MaxAdminEmails} addresses.")
            .When(x => x.AdminEmails is not null);

        // Surrounding whitespace is trimmed here and again by AuthenticationSettings, so only what is
        // inside the address is judged. Blank entries pass and are dropped when the list is stored.
        RuleForEach(x => x.AdminEmails)
            .MaximumLength(255)
            .Must(e => string.IsNullOrWhiteSpace(e) || PlainAddress().IsMatch(e.Trim()))
            .WithMessage("'{PropertyValue}' is not a plain email address. Enter it as name@domain, without a display name, "
                + "spaces, angle brackets or non-ASCII characters.");

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

    /// <summary>Mostly for tidiness: an allowlist is a handful of people, and the whole list
    /// travels in every settings save.</summary>
    public const int MaxAdminEmails = 100;

    // The RFC 5322 dot-atom characters, ASCII only, so what is saved is exactly what a sign-in can
    // match (AdminIdentity.VerifiedEmailOf refuses non-ASCII the same way). An Outlook-style
    // 'Alice Admin <alice@…>' or 'a@b; c@d' — both of which #30's paste-a-list could produce — would
    // otherwise be stored and never match, and the administrator would learn that from "Access
    // denied" after a save that looked fine. Non-ASCII is refused because ToLowerInvariant folds some
    // of it into ASCII: the Kelvin sign (U+212A) becomes 'k', so a lookalike could match a real entry.
    [GeneratedRegex(@"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\z")]
    private static partial Regex PlainAddress();
}
