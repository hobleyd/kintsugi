using System.Security.Claims;
using Kintsugi.Application.Authentication;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Controllers;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.Security;

/// <summary>
/// The administrators allowlist: who counts as an administrator once sign-in is required, and how
/// a user's email is read from the claims. The empty-list case is the one that matters most — it is
/// what makes deploying the feature unable to lock anybody out.
/// </summary>
public class AdminAllowlistTests
{
    private static AuthenticationSettings Settings(bool enabled = true, params string[] admins) =>
        AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, enabled, admins);

    // "name" as the name claim, as DynamicOpenIdConnectOptionsConfigurator sets it, so Identity.Name
    // is the display name here too — otherwise a test that it never leaks would pass vacuously.
    private static ClaimsPrincipal SignedIn(params Claim[] claims) => new(new ClaimsIdentity(claims, "TestAuth", "name", "role"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static Claim Verified() => new("email_verified", "true");

    // What Google sends for a Workspace account, and never for a personal one; AdminIdentity requires
    // it from Google and from a cookie that predates the provider claim.
    private static Claim Workspace() => new("hd", "example.invalid");

    [Fact]
    public void EmptyList_EveryoneSignedInIsAdministrator()
    {
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(Settings(), SignedIn(new Claim("email", "anyone@example.invalid"), Verified(), Workspace())));
    }

    [Fact]
    public void EmptyList_StillRequiresSignIn()
    {
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(Settings(), Anonymous()));
    }

    [Fact]
    public void SignInDisabled_AllowsEveryoneRegardlessOfList()
    {
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(Settings(false, "a@example.invalid"), Anonymous()));
    }

    [Fact]
    public void ListedEmail_IsAdministrator_CaseInsensitively()
    {
        var settings = Settings(true, "Juan.Roman@Example.Invalid");
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(settings, SignedIn(new Claim("email", "JUAN.roman@example.invalid"), Verified(), Workspace())));
    }

    [Fact]
    public void UnlistedEmail_IsNotAdministrator()
    {
        var settings = Settings(true, "admin@example.invalid");
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, SignedIn(new Claim("email", "other@example.invalid"), Verified(), Workspace())));
    }

    [Fact]
    public void ListedButUnverifiedEmail_IsNotAdministrator()
    {
        var settings = Settings(true, "admin@example.invalid");
        var user = SignedIn(new Claim("email", "admin@example.invalid"), new Claim("email_verified", "false"));
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, user));
    }

    // Entra has no email_verified claim, so it alone may omit it. The provider is the one
    // DynamicOpenIdConnectOptionsConfigurator recorded at sign-in, not whichever is configured now.
    [Fact]
    public void Entra_WithoutEmailVerifiedClaim_IsAccepted()
    {
        var user = SignedIn(new Claim("email", "admin@example.invalid"), new Claim(AdminIdentity.ProviderClaim, nameof(AuthProvider.MicrosoftEntra)));
        Assert.Equal("admin@example.invalid", AdminIdentity.VerifiedEmailOf(user));
    }

    // On a provider that lets anyone register any address, "absent" would let an unverified sign-up
    // match an allowlisted email and sign scripts that run as root.
    [Theory]
    [InlineData(nameof(AuthProvider.GoogleWorkspace))]
    [InlineData(nameof(AuthProvider.GenericOidc))]
    [InlineData(nameof(AuthProvider.Clerk))]
    public void OtherProviders_WithoutEmailVerifiedClaim_AreRefused(string provider)
    {
        var settings = Settings(true, "admin@example.invalid");
        var user = SignedIn(new Claim("email", "admin@example.invalid"), new Claim(AdminIdentity.ProviderClaim, provider));
        Assert.Null(AdminIdentity.VerifiedEmailOf(user));
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, user));
    }

    // A cookie issued before the provider claim existed: signs in again rather than being trusted.
    [Fact]
    public void NoProviderClaim_WithoutEmailVerifiedClaim_IsRefused()
    {
        Assert.Null(AdminIdentity.VerifiedEmailOf(SignedIn(new Claim("email", "admin@example.invalid"))));
    }

    // MapUniqueJsonKey renders a JSON boolean as "True".
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void EmailVerified_IsAcceptedInEitherCase(string value)
    {
        var user = SignedIn(new Claim("email", "admin@example.invalid"), new Claim("email_verified", value), Workspace());
        Assert.Equal("admin@example.invalid", AdminIdentity.VerifiedEmailOf(user));
    }

    // An explicit "false" is refused even from Entra.
    [Fact]
    public void EmailVerifiedFalse_IsRefused_EvenFromEntra()
    {
        var user = SignedIn(
            new Claim("email", "admin@example.invalid"),
            new Claim("email_verified", "false"),
            new Claim(AdminIdentity.ProviderClaim, nameof(AuthProvider.MicrosoftEntra)));
        Assert.Null(AdminIdentity.VerifiedEmailOf(user));
    }

    [Fact]
    public void SignedInWithNoEmail_IsNotAdministratorWhenListIsSet()
    {
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(Settings(true, "admin@example.invalid"), SignedIn(new Claim("name", "No Email"))));
    }

    [Fact]
    public void Email_IsReadFromEitherClaimSpelling()
    {
        Assert.Equal("a@example.invalid", AdminIdentity.VerifiedEmailOf(SignedIn(new Claim(ClaimTypes.Email, "A@Example.invalid"), Verified(), Workspace())));
        Assert.Equal("a@example.invalid", AdminIdentity.VerifiedEmailOf(SignedIn(new Claim("email", "a@example.invalid"), Verified(), Workspace())));
    }

    [Fact]
    public void DisplayName_IsTheLocalPart_AndSignerIsTheFullEmail()
    {
        var user = SignedIn(new Claim("email", "juan.roman@example.invalid"), new Claim("name", "Juan Roman"), Verified(), Workspace());
        Assert.Equal("juan.roman", AdminIdentity.DisplayNameOf(user));
        Assert.Equal("juan.roman@example.invalid", AdminIdentity.SignerOf(user));
    }

    [Fact]
    public void Signer_IsNullWhenNobodyIsSignedIn()
    {
        Assert.Null(AdminIdentity.SignerOf(Anonymous()));
    }

    // Without a verified email the signer is the provider's subject, marked unverified — never the
    // display name, which the user can set to someone else's address.
    [Fact]
    public void Signer_WithoutVerifiedEmail_IsTheSubject_NotTheDisplayName()
    {
        var user = SignedIn(new Claim("sub", "abc"), new Claim("name", "alice@example.invalid"));

        var signer = AdminIdentity.SignerOf(user);

        Assert.Equal("unverified account (sub abc)", signer);
        Assert.DoesNotContain("alice", signer);
    }

    // A cookie issued before MapInboundClaims was turned off carries "sub" under its long name.
    [Fact]
    public void Signer_ReadsTheSubjectFromEitherClaimSpelling()
    {
        Assert.Equal("unverified account (sub abc)", AdminIdentity.SignerOf(SignedIn(new Claim(ClaimTypes.NameIdentifier, "abc"))));
    }

    [Fact]
    public void Signer_WithNeitherEmailNorSubject_IsMarkedUnverified()
    {
        Assert.Equal("unverified account", AdminIdentity.SignerOf(SignedIn(new Claim("name", "alice@example.invalid"))));
    }

    // ApprovedScript.SignedBy holds 255 characters.
    [Fact]
    public void Signer_SubjectIsCapped_ToFitTheSignedByColumn()
    {
        var signer = AdminIdentity.SignerOf(SignedIn(new Claim("sub", new string('x', 1000))));

        Assert.NotNull(signer);
        Assert.True(signer.Length <= 255);
    }

    // The remote-control consent dialog and audit record name the requester the same way as a
    // script's signer, so a display name such as "IT Support" never reaches the person at the host.
    [Fact]
    public void RemoteControlRequester_IsTheVerifiedEmail()
    {
        Assert.Equal(
            "juan.roman@example.invalid",
            AdminRemoteControlController.DescribeRequester(SignedIn(new Claim("email", "juan.roman@example.invalid"), Verified(), Workspace(), new Claim("name", "IT Support"))));
    }

    [Fact]
    public void RemoteControlRequester_NeverShowsTheDisplayName()
    {
        var requester = AdminRemoteControlController.DescribeRequester(SignedIn(new Claim("sub", "abc"), new Claim("name", "IT Support")));

        Assert.DoesNotContain("IT Support", requester);
    }

    [Fact]
    public void RemoteControlRequester_SaysSoWhenSignInIsDisabled()
    {
        Assert.Equal("an administrator (sign-in is disabled on this server)", AdminRemoteControlController.DescribeRequester(Anonymous()));
    }

    // ToLowerInvariant folds the Kelvin sign (U+212A) into 'k', so without the ASCII check a lookalike
    // address from a lax provider would match the real entry.
    [Fact]
    public void NonAsciiEmail_DoesNotMatchItsAsciiLookalike()
    {
        var settings = Settings(true, "kelvin@example.invalid");
        var user = SignedIn(new Claim("email", "\u212Aelvin@example.invalid"), Verified(), Workspace(), new Claim("sub", "abc"));

        Assert.Null(AdminIdentity.VerifiedEmailOf(user));
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, user));
        Assert.Equal("unverified account (sub abc)", AdminIdentity.SignerOf(user));
    }

    // Google marks a personal account registered with the work address as verified too; only a
    // Workspace account carries "hd". This holds with no Hosted Domain configured.
    [Fact]
    public void Google_PersonalAccountWithTheWorkAddress_IsRefused()
    {
        var settings = Settings(true, "admin@example.invalid");
        var user = SignedIn(
            new Claim("email", "admin@example.invalid"), Verified(), new Claim(AdminIdentity.ProviderClaim, nameof(AuthProvider.GoogleWorkspace)));

        Assert.Null(AdminIdentity.VerifiedEmailOf(user));
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, user));
    }

    [Fact]
    public void Google_WorkspaceAccount_IsAccepted()
    {
        var user = SignedIn(
            new Claim("email", "admin@example.invalid"), Verified(), Workspace(), new Claim(AdminIdentity.ProviderClaim, nameof(AuthProvider.GoogleWorkspace)));

        Assert.Equal("admin@example.invalid", AdminIdentity.VerifiedEmailOf(user));
    }

    // A cookie from before the provider claim may be Google's, so it is held to Google's rule: a
    // Workspace user's old cookie keeps working, a personal account's does not.
    [Fact]
    public void NoProviderClaim_IsHeldToGooglesRule()
    {
        Assert.Equal("admin@example.invalid", AdminIdentity.VerifiedEmailOf(SignedIn(new Claim("email", "admin@example.invalid"), Verified(), Workspace())));
        Assert.Null(AdminIdentity.VerifiedEmailOf(SignedIn(new Claim("email", "admin@example.invalid"), Verified())));
    }

    // "hd" is a Google concept; other providers are not asked for it.
    [Theory]
    [InlineData(nameof(AuthProvider.GenericOidc))]
    [InlineData(nameof(AuthProvider.Clerk))]
    public void OtherProviders_DoNotNeedHd(string provider)
    {
        var user = SignedIn(new Claim("email", "admin@example.invalid"), Verified(), new Claim(AdminIdentity.ProviderClaim, provider));

        Assert.Equal("admin@example.invalid", AdminIdentity.VerifiedEmailOf(user));
    }

    [Fact]
    public void Update_NullKeepsTheList_EmptyClearsIt_AndEntriesAreNormalised()
    {
        var settings = Settings(true, " A@Example.invalid ", "a@example.invalid", "");
        Assert.Equal(new[] { "a@example.invalid" }, settings.AdminEmails);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true);
        Assert.Single(settings.AdminEmails);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true, Array.Empty<string>());
        Assert.Empty(settings.AdminEmails);
    }
}
