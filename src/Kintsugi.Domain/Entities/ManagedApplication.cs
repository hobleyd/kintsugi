using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// An application that another tool keeps up to date — CrowdStrike for Falcon, Jamf Pro for Self
/// Service+ — so Kintsugi neither researches an upgrade path for it nor patches it.
///
/// Keyed by name alone, across every platform and host: the question being answered is "who owns
/// this product", and the same product is seldom owned by different tools on different platforms
/// in the way that would make a platform part of the key worth the extra step to mark each one.
/// The name is compared without regard to case, as every other application name here is.
///
/// Marking one is a decision with a consequence: it stops Kintsugi patching that application on every
/// host. That is why this is something an administrator does on purpose, one name at a time, and not
/// something inferred.
/// </summary>
public class ManagedApplication : BaseEntity
{
    public const int MaxNameLength = 255;
    public const int MaxOwnerLength = 64;

    public string ApplicationName { get; private set; } = default!;

    /// <summary><see cref="ApplicationName"/> lower-cased, which is what is unique. Kept as its own
    /// column because a unique index over `lower(name)` is not something the model can express.</summary>
    public string NormalizedName { get; private set; } = default!;

    /// <summary>Who patches it: a name from the owner list, as written there.</summary>
    public string Owner { get; private set; } = default!;

    private ManagedApplication()
    {
    }

    public static ManagedApplication Create(string applicationName, string owner)
    {
        var managed = new ManagedApplication();
        managed.ApplicationName = ValidName(applicationName);
        managed.NormalizedName = Normalize(managed.ApplicationName);
        managed.Owner = ValidOwner(owner);
        return managed;
    }

    public void ReassignTo(string owner)
    {
        Owner = ValidOwner(owner);
        MarkUpdated();
    }

    /// <summary>How names are compared: the same function for every lookup, so what is stored and what
    /// is asked for always agree.</summary>
    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    internal static string ValidName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("An application name is required.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new DomainException($"An application name can be at most {MaxNameLength} characters.");
        }

        return trimmed;
    }

    internal static string ValidOwner(string? owner)
    {
        var trimmed = owner?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainException("Say who patches it.");
        }

        if (trimmed.Length > MaxOwnerLength)
        {
            throw new DomainException($"An owner name can be at most {MaxOwnerLength} characters.");
        }

        return trimmed;
    }
}
