using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class AuditSettingsTests
{
    private static AuditSettings Datadog(string? secret = "dd-api-key", string? site = null) =>
        AuditSettings.Create(
            AuditProvider.Datadog, true, null, site, null, secret, null, null, null, null, null, null);

    private static AuditSettings Aws() =>
        AuditSettings.Create(
            AuditProvider.AwsCloudWatch, true, null, "ap-southeast-2", "AKIA", "aws-secret", null, null,
            "/kintsugi/audit", null, null, null);

    [Fact]
    public void Create_ForDatadog_DefaultsTheSiteWhenBlank()
    {
        var settings = Datadog(site: "  ");

        Assert.Equal(AuditSettings.DefaultDatadogSite, settings.Region);
    }

    [Fact]
    public void Create_ForDatadog_RejectsASiteWrittenAsAUrl()
    {
        // The intake URL is built from the site, so a pasted URL would produce
        // https://http-intake.logs.https://app.datadoghq.com/... and fail at the first event.
        Assert.Throws<DomainException>(() => Datadog(site: "https://app.datadoghq.com"));
    }

    [Theory]
    [InlineData(AuditProvider.Datadog)]
    [InlineData(AuditProvider.GoogleCloudLogging)]
    [InlineData(AuditProvider.AwsCloudWatch)]
    [InlineData(AuditProvider.AzureMonitor)]
    [InlineData(AuditProvider.SplunkHec)]
    public void Create_ForAHostedProvider_RequiresASecret(AuditProvider provider)
    {
        Assert.True(AuditSettings.RequiresSecret(provider));
        Assert.Throws<DomainException>(() => AuditSettings.Create(
            provider, true, "https://logs.example.com", "ap-southeast-2", "client", secret: null, "tenant",
            "project", "group", "dcr-1", "Custom-Kintsugi_CL", null));
    }

    [Theory]
    [InlineData(AuditProvider.GrafanaLoki)]
    [InlineData(AuditProvider.GenericHttp)]
    public void Create_ForASelfHostableProvider_AcceptsNoSecret(AuditProvider provider)
    {
        // A self-hosted Loki or an internal collector may take unauthenticated pushes; requiring a
        // token here would rule out the deployments this provider list exists for.
        var settings = AuditSettings.Create(
            provider, true, "http://loki.internal:3100/", null, null, null, null, null, null, null, null, null);

        Assert.Null(settings.Secret);
        Assert.Equal("http://loki.internal:3100", settings.Endpoint);
    }

    [Theory]
    [InlineData(AuditProvider.GrafanaLoki)]
    [InlineData(AuditProvider.AzureMonitor)]
    [InlineData(AuditProvider.SplunkHec)]
    [InlineData(AuditProvider.GenericHttp)]
    public void Create_ForAnEndpointProvider_RejectsANonHttpEndpoint(AuditProvider provider)
    {
        Assert.Throws<DomainException>(() => AuditSettings.Create(
            provider, true, "ftp://logs.example.com", null, "client", "secret", "tenant", null, null,
            "dcr-1", "Custom-Kintsugi_CL", null));
    }

    [Fact]
    public void Create_ForAzureMonitor_RequiresEverythingTheIngestionApiNames()
    {
        // The Logs Ingestion API path is {endpoint}/dataCollectionRules/{dcr}/streams/{stream}, and
        // the token comes from an Entra app — so none of these has a sensible default.
        Assert.Throws<DomainException>(() => AuditSettings.Create(
            AuditProvider.AzureMonitor, true, "https://dce.ingest.monitor.azure.com", null, "app-id",
            "secret", "tenant", null, null, "dcr-1", stream: null, null));
    }

    [Fact]
    public void Update_WithABlankSecret_KeepsTheStoredOneForTheSameProvider()
    {
        var settings = Datadog();

        settings.Update(AuditProvider.Datadog, false, null, "datadoghq.eu", null, "  ", null, null, null, null, null, null);

        Assert.Equal("dd-api-key", settings.Secret);
        Assert.Equal("datadoghq.eu", settings.Region);
        Assert.False(settings.IsEnabled);
    }

    [Fact]
    public void Update_ChangingProvider_DropsTheStoredSecret()
    {
        var settings = Datadog();

        // A Datadog API key is not an AWS secret access key. Carrying it across would ship a
        // credential issued by one vendor to another on the first event, so a provider change has to
        // be accompanied by a new secret.
        var ex = Assert.Throws<DomainException>(() => settings.Update(
            AuditProvider.AwsCloudWatch, true, null, "ap-southeast-2", "AKIA", null, null, null,
            "/kintsugi/audit", null, null, null));
        Assert.Contains("secret access key", ex.Message);

        settings.Update(
            AuditProvider.GrafanaLoki, true, "https://logs-prod-1.grafana.net", null, "123456", null, null, null,
            null, null, null, null);
        Assert.Null(settings.Secret);
    }

    [Fact]
    public void Update_ChangingProvider_ClearsTheFieldsTheNewOneDoesNotRead()
    {
        var settings = Aws();

        settings.Update(
            AuditProvider.SplunkHec, true, "https://splunk.example.com:8088", null, null, "hec-token", null, null,
            null, null, null, "kintsugi");

        // A stale region or log group left behind would be read by nothing today and by something
        // surprising the day the AWS path is chosen again.
        Assert.Null(settings.Region);
        Assert.Null(settings.ClientId);
        Assert.Null(settings.LogGroup);
        Assert.Equal("https://splunk.example.com:8088", settings.Endpoint);
        Assert.Equal("kintsugi", settings.Index);
        Assert.Equal("hec-token", settings.Secret);
    }

    [Fact]
    public void ClearSecret_RemovesIt_AndTheNextUpdateForAHostedProviderRefuses()
    {
        var settings = Datadog();

        settings.ClearSecret();

        Assert.Null(settings.Secret);
        Assert.Throws<DomainException>(() =>
            settings.Update(AuditProvider.Datadog, true, null, null, null, null, null, null, null, null, null, null));
    }
}
