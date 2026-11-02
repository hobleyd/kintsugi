using System.Security.Claims;
using System.Text;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Authentication;

/// <summary>
/// Reads who a signed-in browser user is from their claims — the one place that knows what the
/// claim is called, so the allowlist check, the signer recorded on an approval and the name the
/// admin UI shows cannot disagree.
/// </summary>
/// <remarks>
/// Tolerant of both spellings on purpose. ASP.NET Core's OpenID Connect handler renames incoming
/// claims by default (<c>email</c> becomes <see cref="ClaimTypes.Email"/>), and which spelling the
/// session cookie carries depends on that setting; production showed <c>Identity.Name</c> and
/// <c>FindFirst("email")</c> both null with the defaults (cause inferred, not reproduced). <c>DynamicOpenIdConnectOptionsConfigurator</c>
/// now keeps the provider's own names, but a cookie issued before that change still carries the
/// long ones until it expires, and reading either is cheaper than debugging a lockout.
/// </remarks>
public static class AdminIdentity
{
    /// <summary>
    /// The claim <c>DynamicOpenIdConnectOptionsConfigurator</c> stamps on the identity at sign-in,
    /// naming the <see cref="AuthProvider"/> that issued it. It lets the <c>email_verified</c> rule
    /// below depend on the provider without every caller loading the settings — and it judges the
    /// user by the provider that actually signed them in, not whichever one is configured now.
    /// </summary>
    public const string ProviderClaim = "kintsugi_idp";

    /// <summary>The signed-in user's email, lower-cased, or null when there is none or it is not
    /// known to be verified.</summary>
    /// <remarks>
    /// <para>
    /// Every provider except Microsoft Entra must assert <c>email_verified</c>; a missing claim is
    /// refused, because on a provider that lets anyone register any address, "absent" would let an
    /// unverified sign-up match an allowlisted email and sign scripts that run as root. Entra has
    /// no such claim, so it alone is let through without one. "True" is accepted as well as
    /// "true" — the OIDC handler's <c>MapUniqueJsonKey</c> renders a JSON boolean that way.
    /// </para>
    /// <para>
    /// Google additionally needs <c>hd</c>: it marks a personal account registered with the work
    /// address as verified too, and disabling the Workspace user would not touch that account. Only
    /// a Workspace account carries <c>hd</c>, and a Workspace can only hold addresses in domains it
    /// has proved it owns, so requiring it shuts personal accounts out whether or not a Hosted
    /// Domain is configured. (When one is, <c>DynamicOpenIdConnectOptionsConfigurator</c> also
    /// requires an exact match at sign-in.) It is not compared with the email's domain, because
    /// <c>hd</c> is the organisation's domain and a user on a secondary domain would be locked out.
    /// </para>
    /// <para>
    /// A cookie issued before <see cref="ProviderClaim"/> existed carries no provider and is held to
    /// the strictest rule — <c>email_verified</c> and <c>hd</c>, as for Google — because it may be
    /// Google's. A Workspace user's old cookie passes; anyone else signs in again rather than being
    /// trusted by default.
    /// </para>
    /// </remarks>
    public static string? VerifiedEmailOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var provider = user.FindFirst(ProviderClaim)?.Value;
        var verified = user.FindFirst("email_verified")?.Value;
        var refused = verified is null
            ? provider != nameof(AuthProvider.MicrosoftEntra)
            : !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase);
        if (refused)
        {
            return null;
        }

        if ((provider is null || provider == nameof(AuthProvider.GoogleWorkspace))
            && string.IsNullOrWhiteSpace(user.FindFirst("hd")?.Value))
        {
            return null;
        }

        var email = (user.FindFirst("email") ?? user.FindFirst(ClaimTypes.Email))?.Value?.Trim();

        // ASCII only, matching UpdateAuthenticationSettingsCommandValidator: ToLowerInvariant folds
        // some non-ASCII into ASCII (the Kelvin sign U+212A becomes 'k'), so a lookalike address on a
        // lax provider could otherwise match a listed one.
        if (string.IsNullOrEmpty(email) || !Ascii.IsValid(email))
        {
            return null;
        }

        return email.ToLowerInvariant();
    }

    /// <summary>What the admin UI shows for this user: the part of their email before the
    /// <c>@</c> (<c>juan.roman</c>), falling back to the provider's display name. Display only —
    /// the allowlist and the audit trail use the full email.</summary>
    public static string? DisplayNameOf(ClaimsPrincipal? user)
    {
        var email = VerifiedEmailOf(user);
        if (email is not null)
        {
            var at = email.IndexOf('@');
            return at > 0 ? email[..at] : email;
        }

        return user?.Identity?.Name;
    }

    /// <summary>Who to record as the person behind an action — the signer of a script, the requester
    /// of a remote-control session (<c>AdminRemoteControlController.DescribeRequester</c>): the
    /// verified email, so the record says unambiguously which person. Without one (only possible
    /// while the allowlist is empty) the provider's subject identifier, marked unverified — never the
    /// display name, which the user can set to anything, someone else's address included. Null when
    /// nobody is signed in. The approvals repository is private, but this is the corporate address
    /// of whoever signs — revisit if that changes.</summary>
    public static string? SignerOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var email = VerifiedEmailOf(user);
        if (email is not null)
        {
            return email;
        }

        // "sub" is stable and set by the provider, not the user. Capped so the result fits
        // ApprovedScript.SignedBy (255); real subs are 21 (Google) to 43 (Entra) characters.
        var sub = (user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value?.Trim();
        return string.IsNullOrEmpty(sub)
            ? "unverified account"
            : $"unverified account (sub {sub[..Math.Min(sub.Length, 200)]})";
    }
}
