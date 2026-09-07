using System.Text.RegularExpressions;
using FluentValidation;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Auditing.Commands.UpdateAuditSettings;

/// <summary>
/// Per-field rules for the Auditing settings page. These duplicate what
/// <see cref="Domain.Entities.AuditSettings"/> enforces, deliberately: the entity keeps the
/// invariant true whoever writes to it, and this turns a bad save into a message under the field
/// that caused it rather than one sentence at the top of the page. Keep the two in step — and the
/// screen's instructions, which restate which fields each provider needs, in step with both.
/// </summary>
public partial class UpdateAuditSettingsCommandValidator : AbstractValidator<UpdateAuditSettingsCommand>
{
    private const string UrlMessage = "Enter an absolute http:// or https:// URL.";

    public UpdateAuditSettingsCommandValidator()
    {
        RuleFor(c => c.Provider).IsInEnum();

        RuleFor(c => c.Endpoint).MaximumLength(2048);
        RuleFor(c => c.Region).MaximumLength(128);
        RuleFor(c => c.ClientId).MaximumLength(512);
        RuleFor(c => c.TenantId).MaximumLength(128);
        RuleFor(c => c.ProjectId).MaximumLength(128);
        RuleFor(c => c.LogGroup).MaximumLength(512);
        RuleFor(c => c.DataCollectionRuleId).MaximumLength(128);
        RuleFor(c => c.Stream).MaximumLength(512);
        RuleFor(c => c.Index).MaximumLength(128);

        // Datadog: a site, not a URL. Blank is the default site.
        RuleFor(c => c.Region)
            .Must(site => !site!.Contains("://") && !site.Contains('/'))
            .WithMessage("The Datadog site is a hostname such as datadoghq.com or datadoghq.eu, not a URL.")
            .When(c => c.Provider == AuditProvider.Datadog && !string.IsNullOrWhiteSpace(c.Region));

        // Grafana Loki: a push URL; credentials optional.
        RuleFor(c => c.Endpoint)
            .NotEmpty().WithMessage("The Loki push URL is required.")
            .Must(BeHttpUrl).WithMessage(UrlMessage)
            .When(c => c.Provider == AuditProvider.GrafanaLoki);

        // Google Cloud Logging: a project and a service-account key.
        RuleFor(c => c.ProjectId)
            .NotEmpty().WithMessage("A Google Cloud project ID is required.")
            .When(c => c.Provider == AuditProvider.GoogleCloudLogging);

        // AWS CloudWatch Logs: region, access key pair, log group.
        RuleFor(c => c.Region)
            .NotEmpty().WithMessage("An AWS region is required.")
            .Matches(AwsRegionPattern()).WithMessage("Enter an AWS region such as ap-southeast-2.")
            .When(c => c.Provider == AuditProvider.AwsCloudWatch);
        RuleFor(c => c.ClientId)
            .NotEmpty().WithMessage("An access key ID is required.")
            .When(c => c.Provider == AuditProvider.AwsCloudWatch);
        RuleFor(c => c.LogGroup)
            .NotEmpty().WithMessage("A log group name is required.")
            .When(c => c.Provider == AuditProvider.AwsCloudWatch);

        // Azure Monitor: the Logs Ingestion API needs the endpoint, the rule, the stream and an
        // Entra application to get a token with.
        RuleFor(c => c.Endpoint)
            .NotEmpty().WithMessage("The data collection endpoint's logs-ingestion URL is required.")
            .Must(BeHttpUrl).WithMessage(UrlMessage)
            .When(c => c.Provider == AuditProvider.AzureMonitor);
        RuleFor(c => c.TenantId)
            .NotEmpty().WithMessage("A directory (tenant) ID is required.")
            .When(c => c.Provider == AuditProvider.AzureMonitor);
        RuleFor(c => c.ClientId)
            .NotEmpty().WithMessage("An application (client) ID is required.")
            .When(c => c.Provider == AuditProvider.AzureMonitor);
        RuleFor(c => c.DataCollectionRuleId)
            .NotEmpty().WithMessage("The data collection rule's immutable ID is required.")
            .When(c => c.Provider == AuditProvider.AzureMonitor);
        RuleFor(c => c.Stream)
            .NotEmpty().WithMessage("The stream name declared by the data collection rule is required.")
            .When(c => c.Provider == AuditProvider.AzureMonitor);

        // Splunk HEC and a generic endpoint: a URL.
        RuleFor(c => c.Endpoint)
            .NotEmpty().WithMessage("The HTTP Event Collector URL is required.")
            .Must(BeHttpUrl).WithMessage(UrlMessage)
            .When(c => c.Provider == AuditProvider.SplunkHec);
        RuleFor(c => c.Endpoint)
            .NotEmpty().WithMessage("The endpoint URL is required.")
            .Must(BeHttpUrl).WithMessage(UrlMessage)
            .When(c => c.Provider == AuditProvider.GenericHttp);
    }

    private static bool BeHttpUrl(string? value) =>
        Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Two-letter partition, one or more words, a digit: us-east-1, ap-southeast-2, us-gov-west-1,
    // cn-north-1. Loose on purpose — new regions appear, and the entity does not police this.
    [GeneratedRegex("^[a-z]{2}(-[a-z]+)+-\\d$")]
    private static partial Regex AwsRegionPattern();
}
