using FluentValidation;
using Kintsugi.Application.Authentication.Commands.UpdateAuthenticationSettings;

namespace Kintsugi.Application.Authentication.Commands.AddAdministrators;

public class AddAdministratorsCommandValidator : AbstractValidator<AddAdministratorsCommand>
{
    public AddAdministratorsCommandValidator()
    {
        RuleFor(x => x.Emails).NotEmpty().WithMessage("Enter at least one email address.");

        // The same rule the Authentication screen's list is held to, because both write the one list.
        RuleForEach(x => x.Emails)
            .NotEmpty()
            .MaximumLength(255)
            .Must(UpdateAuthenticationSettingsCommandValidator.IsPlainAddress)
            .WithMessage("'{PropertyValue}' is not a plain email address. Enter it as name@domain, without a display name, "
                + "spaces, angle brackets or non-ASCII characters.");
    }
}
