using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.Vanta;

public class VantaSettingsTests
{
    private static VantaSettings Configured(bool enabled = true) =>
        VantaSettings.Create(
            enabled, "client", "secret", null, "vc-1", "pv-1", "https://kintsugi.example.com", 5.0d, 24);

    [Fact]
    public void Update_WithABlankSecret_KeepsTheStoredOne()
    {
        var settings = Configured();

        settings.Update(true, "client", "   ", null, "vc-1", "pv-1", "https://kintsugi.example.com", null, null);

        // The page never receives the real secret, so blank has to mean "keep" or every save would
        // wipe the credential.
        Assert.Equal("secret", settings.ClientSecret);
    }

    [Fact]
    public void ClearClientSecret_RemovesIt()
    {
        var settings = Configured();

        settings.ClearClientSecret();

        Assert.Null(settings.ClientSecret);
        Assert.False(settings.IsConfigured);
    }

    [Fact]
    public void Update_RefusesToEnableWithoutEverythingASyncNeeds()
    {
        var settings = Configured(enabled: false);

        var ex = Assert.Throws<DomainException>(() =>
            settings.Update(true, "client", null, null, "vc-1", null, "https://kintsugi.example.com", null, null));

        // Enabling a half-filled form would leave the background service failing on a timer with
        // nobody watching.
        Assert.Contains("resource IDs", ex.Message);
    }

    [Fact]
    public void Update_RefusesANonHttpsConsoleUrl()
    {
        var settings = Configured();

        // Vanta requires externalUrl to be HTTPS and rejects the payload otherwise — a failure that
        // would otherwise surface as an opaque 400 a day later.
        Assert.Throws<DomainException>(() =>
            settings.Update(false, "client", null, null, "vc-1", "pv-1", "http://kintsugi.example.com", null, null));
    }

    [Fact]
    public void Update_TrimsATrailingSlashOffEachUrl()
    {
        var settings = Configured();

        settings.Update(
            true, "client", null, "https://api.vanta-gov.com/", "vc-1", "pv-1", "https://kintsugi.example.com/", null, null);

        Assert.Equal("https://api.vanta-gov.com", settings.ApiBaseUrl);
        Assert.Equal("https://kintsugi.example.com", settings.ConsoleBaseUrl);
    }

    [Fact]
    public void Update_RoundsSeverityToTheTenthVantaWillStore()
    {
        var settings = Configured();

        settings.Update(true, "client", null, null, "vc-1", "pv-1", "https://kintsugi.example.com", 7.26d, null);

        Assert.Equal(7.3d, settings.Severity);
    }

    [Fact]
    public void Update_RejectsASeverityOutsideVantasScale()
    {
        var settings = Configured();

        Assert.Throws<DomainException>(() =>
            settings.Update(true, "client", null, null, "vc-1", "pv-1", "https://kintsugi.example.com", 11d, null));
    }
}
