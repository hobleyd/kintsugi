using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One name on the list of tools that can own a managed application — CrowdStrike, Jamf Pro,
/// NinjaOne, Windows Update to begin with. An administrator adds to it and removes from it, so the
/// list matches the tools this organisation actually runs and not the four a developer thought of.
///
/// A list of names rather than an enum, because the point is that it is not fixed. An owner still
/// used by a <see cref="ManagedApplication"/> cannot be removed (see the command that removes one).
/// </summary>
public class ManagedOwner : BaseEntity
{
    public string Name { get; private set; } = default!;

    /// <summary>The name lower-cased, which is what is unique.</summary>
    public string NormalizedName { get; private set; } = default!;

    private ManagedOwner()
    {
    }

    public static ManagedOwner Create(string name)
    {
        var owner = new ManagedOwner { Name = ManagedApplication.ValidOwner(name) };
        owner.NormalizedName = ManagedApplication.Normalize(owner.Name);
        return owner;
    }
}
