using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Ai;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="AiUpgradePathResearchClient"/> against a fake <see cref="HttpMessageHandler"/>
/// (no real network calls, no real API keys) plus, for <c>CheckScriptVersionAsync</c>, a real local
/// bash subprocess (no network involved at all — it just runs a trivial script). Tests avoid
/// setting <c>ApplicationIdentifier</c> so the GitHub/GitLab hosting-site lookup (a separate,
/// best-effort HTTP round trip) never fires, keeping each test to exactly the HTTP call(s) it's
/// actually asserting about. Every fake response uses a script with no `$variable` references at
/// all, so it passes cleanly regardless of whether `shellcheck` happens to be installed in the
/// environment running these tests (the sandbox it was written in doesn't have it — the code
/// itself fails open when shellcheck can't be run, so this is deliberately not testing that path).
/// </summary>
public class AiUpgradePathResearchClientTests
{
    private const string ValidScript = "#!/bin/bash\nset -euo pipefail\n# --appName --appId --update-version --update\necho \"1.0.0\"\n";

    private static readonly AiProviderSettings AnthropicSettings = new(AiProvider.Anthropic, "sk-test-key", null, "claude-sonnet-5");

    private static UpgradePathScriptGenerationRequest Request(string? applicationIdentifier = null, string? promptOverride = null) =>
        new("Firefox", "macOS", new[] { "128.0" }, applicationIdentifier, promptOverride);

    private static HttpResponseMessage AnthropicTextResponse(string text) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = new[] { new { type = "text", text } } }) };

    /// <summary>Dequeues one canned response per <c>SendAsync</c> call, in the order tests queue
    /// them — sufficient here since (with no ApplicationIdentifier set) each test hits exactly the
    /// AI provider endpoint, in a known, sequential order.</summary>
    private class QueueingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders;
        public List<HttpRequestMessage> Requests { get; } = new();

        public QueueingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) =>
            _responders = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responders);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responders.Count == 0)
            {
                throw new InvalidOperationException($"No more canned responses queued, but got another request: {request.Method} {request.RequestUri}");
            }

            return Task.FromResult(_responders.Dequeue()(request));
        }
    }

    private class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw _exception;
    }

    private static AiUpgradePathResearchClient CreateClient(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().Build(),
        FakeGitHubSettings.Provider(),
        Mock.Of<IGooseCliClient>(),
        Mock.Of<IClaudeAgentSdkClient>(),
        NullLogger<AiUpgradePathResearchClient>.Instance);

    [Fact]
    public async Task GenerateScriptAsync_WithAValidScriptOnTheFirstAttempt_ReturnsFoundWithoutAnyRetry()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse(ValidScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(ValidScript.Trim(), result.Script);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheModelReportsNoReliableMethod_ReturnsNotFound()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse("NO_RELIABLE_METHOD"));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Null(result.Script);
    }

    [Fact]
    public async Task GenerateScriptAsync_StripsAMarkdownCodeFence_ThatTheModelWrappedTheScriptIn()
    {
        var fenced = $"```bash\n{ValidScript}```";
        var handler = new QueueingHandler(_ => AnthropicTextResponse(fenced));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.DoesNotContain("```", result.Script);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheFirstAttemptFailsStructuralValidation_RetriesOnceAndSucceeds()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\necho \"1.0.0\"\n"; // no --appName/--appId/etc anywhere
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(missingContractTokens),
            _ => AnthropicTextResponse(ValidScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(ValidScript.Trim(), result.Script);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenBothTheOriginalAndTheFixAttemptFailValidation_Throws()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\necho \"1.0.0\"\n";
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(missingContractTokens),
            _ => AnthropicTextResponse(missingContractTokens));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
        Assert.Contains("self-correction", ex.Message);
    }

    [Fact]
    public async Task GenerateScriptAsync_WithAnEmptyModelResponse_ThrowsRatherThanPersistingAnUnusableScript()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse(""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheProviderReturnsAnErrorStatus_ThrowsExternalServiceException()
    {
        var handler = new QueueingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid api key") });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheProviderIsUnreachable_ThrowsExternalServiceException_NotARawNetworkException()
    {
        var client = CreateClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
    }

    [Fact]
    public void BuildDefaultPrompt_IncludesTheApplicationNamePlatformAndKnownVersions()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", new[] { "128.0", "127.0" }));

        Assert.Contains("Firefox", prompt);
        Assert.Contains("macOS", prompt);
        Assert.Contains("128.0, 127.0", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_WithNoKnownInstalledVersions_SaysUnknownRatherThanAnEmptyList()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", Array.Empty<string>()));

        Assert.Contains("unknown", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForWindows_AsksForAPowerShellScript_ThatStillRunsItsVersionCheckOnLinux()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Windows, Array.Empty<string>()));

        Assert.Contains("PowerShell script", prompt);
        Assert.Contains("script.ps1 --appName", prompt);
        // The constraint that makes server-side version checking possible at all: --update-version
        // has to run under pwsh on this Linux server, so it can't reach for the registry or WMI.
        Assert.Contains("plain Linux server under PowerShell", prompt);
        Assert.Contains("no registry access", prompt);
        Assert.DoesNotContain("#!/bin/bash", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForMacOs_StillAsksForBash()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.MacOs, Array.Empty<string>()));

        Assert.Contains("bash script", prompt);
        Assert.Contains("`#!/bin/bash`", prompt);
        Assert.DoesNotContain("PowerShell", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForWindows_DescribesTheIdentifierAsARegistryKey_NotABundleId()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(
            new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Windows, Array.Empty<string>(), "Mozilla Firefox"));

        Assert.Contains("Mozilla Firefox", prompt);
        Assert.Contains("uninstall registry", prompt);
        Assert.DoesNotContain("macOS bundle ID", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_AsksForBash_AndForbidsAskingTheApiServersOwnPackageManager()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>()));

        Assert.Contains("a Linux application", prompt);
        Assert.Contains("bash script", prompt);
        Assert.DoesNotContain("PowerShell", prompt);

        // The trap that only exists on this platform. --update-version runs on the API server,
        // which is itself a Linux machine, so `apt-cache policy` there does not fail — it answers
        // confidently about the wrong host, and that answer becomes LatestVersion for the whole
        // fleet. macOS and Windows get this for free because their host-local tools simply aren't
        // present on the server.
        Assert.Contains("runs directly on the fleet-management API server", prompt);
        Assert.Contains("apt-cache", prompt);
        Assert.Contains("Never use any of them", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_TellsTheUpdateModeToDetectThePackageManagerRatherThanAssumeApt()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>()));

        Assert.Contains("Do NOT assume a distribution", prompt);
        Assert.Contains("zypper", prompt);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_DescribesTheIdentifierAsTheReportedOne_NotAMacBundleId()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(
            new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>(), "org.mozilla.firefox"));

        Assert.Contains("org.mozilla.firefox", prompt);
        Assert.Contains("Flatpak application ID", prompt);
        Assert.DoesNotContain("macOS bundle ID", prompt);
    }

    /// <summary>
    /// An unrecognizable operating system string lands in <see cref="PlatformBucket.Generic"/>, and
    /// that bucket has always been prompted for as macOS. Adding a third platform must not have
    /// quietly changed which one it falls back to.
    /// </summary>
    [Fact]
    public void BuildDefaultPrompt_ForTheGenericBucket_StillPromptsAsMacOs()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Generic, Array.Empty<string>()));

        Assert.Contains("a macOS application", prompt);
        Assert.Contains("`#!/bin/bash`", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_IncludesTheApplicationIdentifier_WhenProvided()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", Array.Empty<string>(), "org.mozilla.firefox"));

        Assert.Contains("org.mozilla.firefox", prompt);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithAWellBehavedScript_ReturnsTheBareVersionString()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"129.0\"\nexit 0\n";

        var result = await client.CheckScriptVersionAsync(script, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Equal("129.0", result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithANonZeroExit_ReturnsNull()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"broken\" >&2\nexit 1\n";

        var result = await client.CheckScriptVersionAsync(script, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithMultiLineStdout_ReturnsNull_RatherThanTheFirstLine()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"129.0\"\necho \"unexpected extra line\"\n";

        var result = await client.CheckScriptVersionAsync(script, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithBlankStdout_ReturnsNull()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\nexit 0\n";

        var result = await client.CheckScriptVersionAsync(script, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }
}
