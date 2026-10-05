using FluentValidation;

namespace Kintsugi.Application.AgentPackages.Commands.PublishAgentPackage;

public class PublishAgentPackageCommandValidator : AbstractValidator<PublishAgentPackageCommand>
{
    /// <summary>What a published package may be called on disk: the release archives CI names
    /// (<c>kintsugi-agent-linux-v0.13.0.tar.gz</c>) and nothing that could be a path.</summary>
    public const string FileNamePattern = "^[A-Za-z0-9_-][A-Za-z0-9._-]*$";

    public PublishAgentPackageCommandValidator()
    {
        RuleFor(x => x.Platform)
            .NotEmpty()
            .MaximumLength(32)
            .Matches("^[a-zA-Z0-9-]+$").WithMessage("Platform may only contain letters, numbers, and hyphens.");

        RuleFor(x => x.Version).NotEmpty().MaximumLength(64);
        // A bare file name and nothing else. AgentPackageFileStorage joins this onto the platform
        // directory, and before this rule existed a FileName of "../something" wrote one level above
        // it — verified live against a deployment, landing a file beside the platform directories
        // on the volume that also holds the fleet CA's private key. The first character is
        // restricted separately so "." and ".." (both of which the character class alone would
        // admit) and hidden files are refused outright. AgentPackageFileStorage checks again with
        // Path.GetFileName, because a storage class must not depend on a validator somewhere else
        // having run — see the import path, which also publishes.
        RuleFor(x => x.FileName)
            .NotEmpty()
            .MaximumLength(255)
            .Matches(FileNamePattern)
            .WithMessage("File name may only contain letters, numbers, dots, underscores and hyphens, and may not start with a dot.");
        RuleFor(x => x.ReleaseNotes).MaximumLength(2000);
        RuleFor(x => x.Content).NotNull();
    }
}
