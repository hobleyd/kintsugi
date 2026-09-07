using FluentValidation.TestHelper;
using Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Auditing;

public class UpdateAuditSettingsCommandValidatorTests
{
    private readonly UpdateAuditSettingsCommandValidator _validator = new();

    private static UpdateAuditSettingsCommand Command(
        AuditProvider provider,
        string? endpoint = null,
        string? region = null,
        string? clientId = null,
        string? secret = "secret",
        string? tenantId = null,
        string? projectId = null,
        string? logGroup = null,
        string? dataCollectionRuleId = null,
        string? stream = null,
        string? index = null) =>
        new(provider, true, endpoint, region, clientId, secret, false, tenantId, projectId, logGroup,
            dataCollectionRuleId, stream, index);

    [Fact]
    public void Datadog_WithOnlyAnApiKey_IsValid()
    {
        _validator.TestValidate(Command(AuditProvider.Datadog)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Datadog_WithASiteWrittenAsAUrl_IsRejectedOnTheSiteField()
    {
        _validator.TestValidate(Command(AuditProvider.Datadog, region: "https://app.datadoghq.com"))
            .ShouldHaveValidationErrorFor(c => c.Region);
    }

    [Fact]
    public void GrafanaLoki_WithNoEndpoint_IsRejected()
    {
        _validator.TestValidate(Command(AuditProvider.GrafanaLoki)).ShouldHaveValidationErrorFor(c => c.Endpoint);
    }

    [Fact]
    public void GrafanaLoki_WithAPlainHttpEndpointAndNoCredentials_IsAccepted()
    {
        _validator.TestValidate(Command(AuditProvider.GrafanaLoki, endpoint: "http://loki.internal:3100", secret: null))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AwsCloudWatch_WithARegionThatIsNotOne_IsRejectedOnTheRegionField()
    {
        _validator.TestValidate(Command(AuditProvider.AwsCloudWatch, region: "Sydney", clientId: "AKIA", logGroup: "/kintsugi"))
            .ShouldHaveValidationErrorFor(c => c.Region);
    }

    [Fact]
    public void AwsCloudWatch_WithEverything_IsAccepted()
    {
        _validator.TestValidate(Command(AuditProvider.AwsCloudWatch, region: "us-gov-west-1", clientId: "AKIA", logGroup: "/kintsugi"))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void AzureMonitor_MissingEachOfItsFields_IsRejectedOnThatField()
    {
        var result = _validator.TestValidate(Command(AuditProvider.AzureMonitor));

        result.ShouldHaveValidationErrorFor(c => c.Endpoint);
        result.ShouldHaveValidationErrorFor(c => c.TenantId);
        result.ShouldHaveValidationErrorFor(c => c.ClientId);
        result.ShouldHaveValidationErrorFor(c => c.DataCollectionRuleId);
        result.ShouldHaveValidationErrorFor(c => c.Stream);
    }

    [Fact]
    public void GoogleCloudLogging_WithNoProject_IsRejectedOnTheProjectField()
    {
        _validator.TestValidate(Command(AuditProvider.GoogleCloudLogging)).ShouldHaveValidationErrorFor(c => c.ProjectId);
    }

    [Fact]
    public void FieldsAnotherProviderNeeds_AreNotRequiredOfThisOne()
    {
        // The form only shows the selected provider's fields, so a rule that leaked across providers
        // would report an error under a box the operator cannot see.
        _validator.TestValidate(Command(AuditProvider.SplunkHec, endpoint: "https://splunk.example.com:8088"))
            .ShouldNotHaveAnyValidationErrors();
    }
}
