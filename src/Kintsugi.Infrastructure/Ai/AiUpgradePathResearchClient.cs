using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai;

/// <summary>
/// Generates each application's durable upgrade script in a single AI call — no separate JSON
/// research step — by asking the configured AI provider. Anthropic and OpenAI are given their
/// respective hosted web-search tool, since finding a current version number and download
/// location is exactly the kind of question a model shouldn't answer from training data alone.
/// Ollama gets the same capability via Ollama's hosted web search API when the
/// OLLAMA_WEB_API_KEY configuration value is set: the local model is offered a web_search tool,
/// and any tool call it makes is executed against that API and fed back for a final answer.
/// Without that key, Ollama falls back to answering from what it already knows and is told to
/// flag that plainly as a comment in the script itself — those results should be treated as a
/// starting point, not a verified fact. Goose is reached over ACP against a `goose serve` instance
/// rather than a local subprocess — see <see cref="GooseCliClient"/> — since it manages its own
/// provider/web-search configuration outside this system; the prompt is simply handed to it as-is
/// and its reply text is treated the same way as any other provider's answer.
/// </summary>
public class AiUpgradePathResearchClient : IUpgradePathResearchClient
{
    private const string OllamaWebSearchUrl = "https://ollama.com/api/web_search";

    private static readonly JsonSerializerOptions ModelResultJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IGooseCliClient _gooseCliClient;
    private readonly ILogger<AiUpgradePathResearchClient> _logger;

    public AiUpgradePathResearchClient(HttpClient httpClient, IConfiguration configuration, IGooseCliClient gooseCliClient, ILogger<AiUpgradePathResearchClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _configuration = configuration;
        _gooseCliClient = gooseCliClient;
        _logger = logger;
    }

    public string BuildDefaultPrompt(UpgradePathScriptGenerationRequest request) => BuildScriptGenerationPrompt(request, hostingSiteContext: null);

    private static readonly TimeSpan ScriptCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The only AI call per application — researches how it distributes and checks for updates,
    /// and produces the durable script directly (no separate JSON research step). Validates the
    /// result with shellcheck and, since this script may go on to run unattended with real
    /// privileges on a managed fleet (see the macOS agent's `auto_upgrade` setting), gives the
    /// model exactly one self-correction pass (see <see cref="BuildScriptFixPrompt"/>) before
    /// giving up entirely — see the class doc for why that's thrown rather than returned.
    /// </summary>
    public async Task<UpgradePathScriptResult> GenerateScriptAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, CancellationToken cancellationToken)
    {
        // Looked up once regardless of provider — the model's own hosted web search (where
        // supported) may or may not think to check code-hosting sites specifically, so this hands
        // it concrete GitHub/GitLab candidates for the application's identifier up front rather
        // than leaving it to chance. Skipped when the prompt itself is being overridden, since the
        // override replaces the prompt this context would have been woven into.
        var hostingSiteContext = string.IsNullOrWhiteSpace(request.PromptOverride)
            ? await BuildHostingSiteContextAsync(request.ApplicationIdentifier, cancellationToken)
            : null;

        var text = await AskProviderWithSearchAsync(settings, request, hostingSiteContext, cancellationToken);

        if (IsNoReliableMethodSentinel(text))
        {
            _logger.LogInformation("The model reported no reliable upgrade method for {ApplicationName} ({Platform})", request.ApplicationName, request.Platform);
            return new UpgradePathScriptResult(UpgradePathStatus.NotFound, null, "The AI could not determine a reliable way to check for or install updates to this application.");
        }

        var script = CleanScriptText(text)
            ?? throw new ExternalServiceException("The model's response did not contain a usable script.");

        var (isValid, errors) = await ValidateScriptAsync(script, cancellationToken);
        if (isValid)
        {
            _logger.LogInformation("Script generated for {ApplicationName} ({Platform}) passed validation on the first attempt", request.ApplicationName, request.Platform);
            return new UpgradePathScriptResult(UpgradePathStatus.Found, script, null);
        }

        _logger.LogWarning("Script generated for {ApplicationName} ({Platform}) failed validation, retrying once: {Errors}", request.ApplicationName, request.Platform, errors);

        var fixedScript = CleanScriptText(await AskProviderRawAsync(settings, BuildScriptFixPrompt(request, script, errors!), cancellationToken))
            ?? throw new ExternalServiceException("The model's fix attempt did not contain a usable script.");

        var (fixedIsValid, fixedErrors) = await ValidateScriptAsync(fixedScript, cancellationToken);
        if (!fixedIsValid)
        {
            throw new ExternalServiceException($"The generated script still failed validation after one self-correction attempt: {fixedErrors}");
        }

        _logger.LogInformation("Script for {ApplicationName} ({Platform}) passed validation after one self-correction pass", request.ApplicationName, request.Platform);
        return new UpgradePathScriptResult(UpgradePathStatus.Found, fixedScript, null);
    }

    /// <summary>
    /// Runs an already-generated script's own `--update-version` mode as a subprocess, right here
    /// on the server — the whole point of a durable script is that this never needs another AI
    /// call. Returns null on anything that isn't a clean, single-line version string on stdout: a
    /// non-zero exit, a timeout, or unexpected output — callers should treat null as "the script
    /// broke" and fall back to regenerating it via <see cref="GenerateScriptAsync"/>.
    /// </summary>
    public async Task<string?> CheckScriptVersionAsync(string script, string applicationName, string applicationIdentifier, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"upgrade-check-{Guid.NewGuid():N}.sh");

        try
        {
            await File.WriteAllTextAsync(tempFile, script, cancellationToken);
            File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(tempFile);
            startInfo.ArgumentList.Add("--appName");
            startInfo.ArgumentList.Add(applicationName);
            startInfo.ArgumentList.Add("--appId");
            startInfo.ArgumentList.Add(applicationIdentifier);
            startInfo.ArgumentList.Add("--update-version");

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ScriptCheckTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("--update-version for {ApplicationName} timed out after {Timeout}", applicationName, ScriptCheckTimeout);
                TryKill(process);
                return null;
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("--update-version for {ApplicationName} exited {ExitCode}: {Stderr}", applicationName, process.ExitCode, stderr);
                return null;
            }

            if (string.IsNullOrWhiteSpace(stdout) || stdout.Contains('\n'))
            {
                _logger.LogWarning("--update-version for {ApplicationName} produced unexpected output: {Stdout}", applicationName, stdout);
                return null;
            }

            return stdout;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            _logger.LogWarning(ex, "Could not run --update-version for {ApplicationName}", applicationName);
            return null;
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — it may already have exited.
        }
    }

    private Task<string> AskProviderWithSearchAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, string? hostingSiteContext, CancellationToken cancellationToken) => settings.Provider switch
    {
        AiProvider.Anthropic => AskAnthropicWithSearchAsync(settings, request, hostingSiteContext, cancellationToken),
        AiProvider.OpenAI => AskOpenAiWithSearchAsync(settings, request, hostingSiteContext, cancellationToken),
        AiProvider.Ollama => AskOllamaWithSearchAsync(settings, request, hostingSiteContext, cancellationToken),
        AiProvider.GooseCli => _gooseCliClient.RunAsync(ResolvePrompt(request, hostingSiteContext), settings.Model, settings.BaseUrl, cancellationToken),
        _ => throw new ExternalServiceException($"Unsupported AI provider '{settings.Provider}'.")
    };

    private Task<string> AskProviderRawAsync(AiProviderSettings settings, string prompt, CancellationToken cancellationToken) => settings.Provider switch
    {
        AiProvider.Anthropic => AskAnthropicRawAsync(settings, prompt, cancellationToken),
        AiProvider.OpenAI => AskOpenAiRawAsync(settings, prompt, cancellationToken),
        AiProvider.Ollama => AskOllamaRawAsync(settings, prompt, cancellationToken),
        AiProvider.GooseCli => _gooseCliClient.RunAsync(prompt, settings.Model, settings.BaseUrl, cancellationToken),
        _ => throw new ExternalServiceException($"Unsupported AI provider '{settings.Provider}'.")
    };

    private async Task<string> AskAnthropicWithSearchAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, string? hostingSiteContext, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "claude-sonnet-4-5" : settings.Model,
            max_tokens = 8192,
            tools = new object[] { new { type = "web_search_20250305", name = "web_search", max_uses = 3 } },
            messages = new object[] { new { role = "user", content = ResolvePrompt(request, hostingSiteContext) } }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        httpRequest.Headers.Add("x-api-key", settings.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken: cancellationToken);

        return string.Join("\n", (body?.Content ?? new())
            .Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text))
            .Select(b => b.Text));
    }

    private async Task<string> AskOpenAiWithSearchAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, string? hostingSiteContext, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-5" : settings.Model,
            input = ResolvePrompt(request, hostingSiteContext),
            tools = new object[] { new { type = "web_search_preview" } },
            max_output_tokens = 8192
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken: cancellationToken);

        return string.Join("\n", (body?.Output ?? new())
            .Where(o => o.Type == "message")
            .SelectMany(o => o.Content ?? new())
            .Where(c => c.Type == "output_text" && !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text));
    }

    private async Task<string> AskOllamaWithSearchAsync(AiProviderSettings settings, UpgradePathScriptGenerationRequest request, string? hostingSiteContext, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new ExternalServiceException("No Ollama endpoint URL is configured.");
        }

        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/api/chat", UriKind.Absolute, out var uri))
        {
            throw new ExternalServiceException("The configured Ollama endpoint URL is not valid.");
        }

        var webSearchApiKey = _configuration["OLLAMA_WEB_API_KEY"];
        var hasWebSearch = !string.IsNullOrWhiteSpace(webSearchApiKey);

        var basePrompt = ResolvePrompt(request, hostingSiteContext);
        var prompt = hasWebSearch
            ? basePrompt +
                "\n\nUse the web_search tool to confirm the current version and download/release-notes " +
                "URL before answering — don't rely on training data alone for those."
            : basePrompt +
                "\n\nNote: you do not have live web access for this request — write your best script " +
                "anyway, using what you already know, but add a \"# WARNING: ...\" comment near the top " +
                "explaining that the version or URL details may be out of date.";

        var messages = new List<object> { new { role = "user", content = prompt } };
        var tools = hasWebSearch ? BuildOllamaWebSearchTool() : null;

        var body = await SendOllamaChatAsync(uri, settings.Model, messages, tools, format: null, cancellationToken);

        if (hasWebSearch && body?.Message?.ToolCalls is { Count: > 0 } toolCalls)
        {
            messages.Add(new
            {
                role = "assistant",
                content = body.Message.Content ?? string.Empty,
                tool_calls = toolCalls
                    .Select(call => new { function = new { name = call.Function?.Name, arguments = call.Function?.Arguments } })
                    .ToList()
            });

            foreach (var call in toolCalls)
            {
                var query = ExtractQueryArgument(call.Function?.Arguments) ?? $"{request.ApplicationName} latest version";
                var results = await SearchOllamaWebAsync(query, webSearchApiKey!, cancellationToken);
                messages.Add(new { role = "tool", content = results });
            }

            body = await SendOllamaChatAsync(uri, settings.Model, messages, tools: null, format: null, cancellationToken);
        }

        return body?.Message?.Content ?? string.Empty;
    }

    /// <summary>Plain prompt-in, text-out completions for the one-shot self-correction retry —
    /// deliberately without the web_search tooling the "with search" methods above use, since
    /// fixing validation findings in an already-researched script is a code-fix task over facts
    /// already in the prompt, not a research one.</summary>
    private async Task<string> AskAnthropicRawAsync(AiProviderSettings settings, string prompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "claude-sonnet-4-5" : settings.Model,
            max_tokens = 4096,
            messages = new object[] { new { role = "user", content = prompt } }
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        httpRequest.Headers.Add("x-api-key", settings.ApiKey);
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<AnthropicResponse>(cancellationToken: cancellationToken);

        return string.Join("\n", (body?.Content ?? new())
            .Where(b => b.Type == "text" && !string.IsNullOrEmpty(b.Text))
            .Select(b => b.Text));
    }

    private async Task<string> AskOpenAiRawAsync(AiProviderSettings settings, string prompt, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-5" : settings.Model,
            input = prompt
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        httpRequest.Content = JsonContent.Create(payload);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<OpenAiResponse>(cancellationToken: cancellationToken);

        return string.Join("\n", (body?.Output ?? new())
            .Where(o => o.Type == "message")
            .SelectMany(o => o.Content ?? new())
            .Where(c => c.Type == "output_text" && !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text));
    }

    private async Task<string> AskOllamaRawAsync(AiProviderSettings settings, string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            throw new ExternalServiceException("No Ollama endpoint URL is configured.");
        }

        if (!Uri.TryCreate(settings.BaseUrl.TrimEnd('/') + "/api/chat", UriKind.Absolute, out var uri))
        {
            throw new ExternalServiceException("The configured Ollama endpoint URL is not valid.");
        }

        var messages = new List<object> { new { role = "user", content = prompt } };
        var body = await SendOllamaChatAsync(uri, settings.Model, messages, tools: null, format: null, cancellationToken);
        return body?.Message?.Content ?? string.Empty;
    }

    private static object[] BuildOllamaWebSearchTool() => new object[]
    {
        new
        {
            type = "function",
            function = new
            {
                name = "web_search",
                description = "Search the public web for current information and return matching pages.",
                parameters = new
                {
                    type = "object",
                    properties = new { query = new { type = "string", description = "The search query." } },
                    required = new[] { "query" }
                }
            }
        }
    };

    private async Task<OllamaChatResponse?> SendOllamaChatAsync(Uri uri, string? model, List<object> messages, object? tools, string? format, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };

        if (tools is not null)
        {
            payload["tools"] = tools;
        }

        if (format is not null)
        {
            payload["format"] = format;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(payload) };
        using var response = await SendAsync(httpRequest, cancellationToken);
        return await response.Content.ReadFromJsonAsync<OllamaChatResponse>(cancellationToken: cancellationToken);
    }

    private async Task<string> SearchOllamaWebAsync(string query, string apiKey, CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, OllamaWebSearchUrl)
        {
            Content = JsonContent.Create(new { query, max_results = 5 })
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<OllamaWebSearchResponse>(cancellationToken: cancellationToken);

        var results = (body?.Results ?? new())
            .Select(r => new { title = r.Title, url = r.Url, content = Truncate(r.Content, 800) });

        return JsonSerializer.Serialize(results, ModelResultJsonOptions);
    }

    private static string? Truncate(string? text, int maxLength) =>
        string.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text[..maxLength];

    private static string? ExtractQueryArgument(JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        return element.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ExternalServiceException($"Could not reach the AI provider: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ExternalServiceException($"AI provider request failed (HTTP {(int)response.StatusCode}): {errorBody}");
        }

        return response;
    }

    /// <summary>
    /// Searches GitHub and GitLab's public repository-search APIs for the application's bundle
    /// identifier, so the model is handed concrete candidate repositories up front instead of
    /// relying entirely on its own (provider-dependent, sometimes absent) web search to think to
    /// check code-hosting sites. Best-effort: any failure (network, rate limiting, either site
    /// being unreachable) is swallowed and simply omits that site's results, since this is an
    /// enrichment step, not something worth failing the whole research request over.
    /// </summary>
    private async Task<string?> BuildHostingSiteContextAsync(string? applicationIdentifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(applicationIdentifier))
        {
            return null;
        }

        var sections = new List<string>();

        var gitHubResults = await SearchGitHubAsync(applicationIdentifier, cancellationToken);
        if (gitHubResults is { Count: > 0 })
        {
            sections.Add("GitHub repositories:\n" + JsonSerializer.Serialize(gitHubResults, ModelResultJsonOptions));
        }

        var gitLabResults = await SearchGitLabAsync(applicationIdentifier, cancellationToken);
        if (gitLabResults is { Count: > 0 })
        {
            sections.Add("GitLab projects:\n" + JsonSerializer.Serialize(gitLabResults, ModelResultJsonOptions));
        }

        return sections.Count > 0 ? string.Join("\n\n", sections) : null;
    }

    private async Task<List<HostingRepoResult>?> SearchGitHubAsync(string applicationIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            var query = Uri.EscapeDataString($"{applicationIdentifier} in:name,description,readme");
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.github.com/search/repositories?q={query}&sort=stars&order=desc&per_page=5");
            httpRequest.Headers.UserAgent.ParseAdd("kintsugi-patching-system");
            httpRequest.Headers.Accept.ParseAdd("application/vnd.github+json");

            var token = _configuration["GITHUB_API_TOKEN"];
            if (!string.IsNullOrWhiteSpace(token))
            {
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<GitHubSearchResponse>(cancellationToken: cancellationToken);
            return body?.Items?
                .Select(i => new HostingRepoResult(i.FullName ?? i.Name ?? "unknown", i.HtmlUrl, i.Description, i.StargazersCount))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<List<HostingRepoResult>?> SearchGitLabAsync(string applicationIdentifier, CancellationToken cancellationToken)
    {
        try
        {
            var query = Uri.EscapeDataString(applicationIdentifier);
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Get, $"https://gitlab.com/api/v4/projects?search={query}&order_by=star_count&sort=desc&per_page=5");
            httpRequest.Headers.UserAgent.ParseAdd("kintsugi-patching-system");

            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var items = await response.Content.ReadFromJsonAsync<List<GitLabProjectItem>>(cancellationToken: cancellationToken);
            return items?
                .Select(i => new HostingRepoResult(i.PathWithNamespace ?? i.Name ?? "unknown", i.WebUrl, i.Description, i.StarCount))
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private const string NoReliableMethodSentinel = "NO_RELIABLE_METHOD";

    private static bool IsNoReliableMethodSentinel(string text) =>
        text.Trim().Equals(NoReliableMethodSentinel, StringComparison.Ordinal);

    private static string ResolvePrompt(UpgradePathScriptGenerationRequest request, string? hostingSiteContext) =>
        string.IsNullOrWhiteSpace(request.PromptOverride) ? BuildScriptGenerationPrompt(request, hostingSiteContext) : request.PromptOverride;

    private static string BuildScriptGenerationPrompt(UpgradePathScriptGenerationRequest request, string? hostingSiteContext)
    {
        var versions = request.KnownInstalledVersions.Count > 0
            ? string.Join(", ", request.KnownInstalledVersions)
            : "unknown";

        var identifierLine = string.IsNullOrWhiteSpace(request.ApplicationIdentifier)
            ? ""
            : $"\nApplication identifier (macOS bundle ID): {request.ApplicationIdentifier}";

        var hostingSection = string.IsNullOrWhiteSpace(hostingSiteContext)
            ? ""
            : $$"""


                Candidate repositories found by searching GitHub and GitLab for this application's
                identifier (a name match doesn't guarantee it's the right project — verify relevance,
                e.g. via its description or README, before relying on it):
                {{hostingSiteContext}}

                """;

        return $$"""
            You are researching how a macOS application distributes and checks for updates, then
            writing a durable, reusable command-line tool that performs both jobs for a
            fleet-management system. It will be invoked repeatedly and unattended, indefinitely into
            the future — long after new versions of this application are released — so it must
            discover the current latest version itself every time it runs rather than having one
            baked in. This is the only research pass; everything the tool needs to keep working
            later must be encoded in the script itself.

            Application: {{request.ApplicationName}}{{identifierLine}}
            Platform: {{request.Platform}}
            Currently installed version(s) seen across managed hosts: {{versions}}
            {{hostingSection}}
            Research how this application distributes updates and how an administrator (or an
            unattended automation agent acting on their behalf) would check for and install the
            latest version. Prefer the vendor's own site or official release notes/hosting (e.g. a
            GitHub or GitLab releases page) over third-party mirrors. If an application identifier is
            given above, use it as a disambiguating search term — especially for generically-named
            applications — and check whether it corresponds to an open-source project hosted on
            GitHub, GitLab, or a similar site.

            If you cannot find a reliable way to check for or install updates to this application at
            all, respond with ONLY this exact line and nothing else: {{NoReliableMethodSentinel}}

            Otherwise, write a single bash script implementing this exact CLI contract:

              script.sh --appName <name> --appId <bundle-id> --update-version
              script.sh --appName <name> --appId <bundle-id> --update

            `--appName` and `--appId` are always both required, along with exactly one of
            `--update-version` or `--update` (in either order). On missing/invalid/conflicting
            arguments, print a one-line usage message to stderr and exit non-zero — no other output.

            `--update-version` mode — this runs directly on a plain Linux server, NOT on a Mac,
            purely to check for a new release, so it MUST NOT use any macOS-only tool (no
            `defaults`, `osascript`, `hdiutil`, `plutil`, `installer`, etc.) or touch anything on the
            filesystem:
            - Determine the current latest stable released version using only `curl` and plain text
              processing (`grep`, `sed`, `cut`, `head`, etc. — assume no `jq`). For a GitHub-hosted
              project, the simplest reliable approach is
              `curl -fsSL -o /dev/null -w '%{redirect_url}' https://github.com/<owner>/<repo>/releases/latest`,
              which returns a URL ending in the latest tag with no JSON parsing at all — prefer this
              kind of redirect/text trick over parsing a JSON API response. Use your own judgement
              based on where this application is actually distributed.
            - On success, print ONLY the bare version string to stdout (nothing else — no labels, no
              extra lines) and exit 0.
            - On failure to determine it, print an error to stderr and exit non-zero. No stdout output.
            - Must not modify anything, or depend on anything being installed or mounted — this mode
              only checks and reports, from a plain Linux shell with curl available.

            `--update` mode — this one DOES run on the managed Mac itself, so macOS tools are fine here:
            - Re-run the same latest-version check as `--update-version` internally.
            - Determine the currently installed version (e.g. via `defaults read
              /Applications/<appName>.app/Contents/Info.plist CFBundleShortVersionString`), and
              verify the installed bundle's CFBundleIdentifier matches `--appId` before touching
              anything — treat a mismatch as a fatal error (wrong app), not something to silently
              ignore.
            - If already at or above the latest version, print a message and exit 0 without
              downloading or changing anything (idempotent).
            - If the application is currently running, quit it gracefully before replacing it —
              already-authorized, so this can be automatic. Use
              `osascript -e "tell application \"<appName>\" to quit"`, then poll for the process to
              actually exit for a bounded grace period (e.g. up to 15s, checking every second via
              `pgrep -x`), and only as a last-resort fallback after that grace period use `pkill -x`
              if it's still running — never skip straight to a hard kill.
            - Download the current release into a directory made with `mktemp -d`, cleaned up via a
              `trap` covering both success and failure. Prefer a stable "latest" URL pattern (e.g.
              GitHub's `.../releases/latest/download/<asset-filename>`, which resolves to whatever
              is current without needing the version number) over constructing a per-version URL
              from the discovered version string, when the distribution channel supports it.
            - For a .dmg: mount with `hdiutil attach -nobrowse -quiet`, copy the .app bundle into
              /Applications (replacing any existing install), detach the volume, then remove the
              quarantine attribute (`xattr -dr com.apple.quarantine "/Applications/<appName>.app"`)
              since this is very likely a Developer-ID/unsigned distribution, not a Mac App Store one.
            - For a .pkg: install via `installer -pkg <path> -target /`.
            - For any other distribution form, use your best judgement for the equivalent
              non-interactive macOS approach.
            - End by verifying the installed version now meets the latest version determined above,
              and exit non-zero with a clear error on stderr if it doesn't.

            General requirements:
            - Start with `#!/bin/bash` and `set -euo pipefail`.
            - No interactive prompts of any kind anywhere in the script — it always runs unattended,
              typically as root or an admin user (for `--update`) or on a plain Linux server (for
              `--update-version`).
            - If you have any caveat about your confidence in this script (e.g. you lacked live web
              access, or found conflicting version numbers), say so in a `# WARNING: ...` comment
              near the top rather than in any separate response text — the script is the only thing
              that gets kept.
            - Output ONLY the script itself — no explanation before or after it, and no markdown
              code fences.
            """;
    }

    private static string BuildScriptFixPrompt(UpgradePathScriptGenerationRequest request, string script, string validationErrors) => $$"""
        The bash script below, which you wrote as a reusable --update-version/--update tool for
        "{{request.ApplicationName}}" on macOS, has issues found during validation. Fix every one
        of them and return the complete corrected script — don't just patch around the symptom if
        a finding points at a real bug (e.g. a typo'd variable name) or a missing part of the
        required CLI contract (--appName, --appId, --update-version, --update). Remember that
        --update-version must run correctly on a plain Linux server with only curl available — no
        macOS-only tools in that mode.

        Original script:
        ```bash
        {{script}}
        ```

        Validation findings:
        {{validationErrors}}

        Output ONLY the corrected, complete script — no explanation, no markdown code fences.
        """;

    /// <summary>Runs shellcheck against <paramref name="script"/> at warning severity and above —
    /// this is what caught a real bug (a typo'd variable name that would have failed every run
    /// under `set -u`) in testing, which "error"-only severity would have missed, since ShellCheck
    /// classifies most logic bugs as warnings rather than errors. If shellcheck itself can't be
    /// run (not installed, etc.), this fails open — reports valid rather than silently discarding
    /// every generated script over a missing tool.</summary>
    private static readonly string[] RequiredCliContractTokens = { "--appName", "--appId", "--update-version", "--update" };

    private static async Task<(bool IsValid, string? Errors)> ValidateScriptAsync(string script, CancellationToken cancellationToken)
    {
        var missingCliTokens = RequiredCliContractTokens.Where(token => !script.Contains(token, StringComparison.Ordinal)).ToList();
        var unassignedNames = FindUnassignedVariableReferences(script);
        var (shellcheckOk, shellcheckErrors) = await RunShellcheckAsync(script, cancellationToken);

        if (missingCliTokens.Count == 0 && unassignedNames.Count == 0 && shellcheckOk)
        {
            return (true, null);
        }

        var errorParts = new List<string>();

        if (missingCliTokens.Count > 0)
        {
            errorParts.Add(
                "Structural check: the script is missing required parts of its CLI contract — no " +
                "reference to " + string.Join(", ", missingCliTokens) + " was found anywhere in the " +
                "script. It must accept --appName and --appId, plus support both an --update-version " +
                "mode and an --update mode.");
        }

        if (unassignedNames.Count > 0)
        {
            errorParts.Add(
                "Custom check: the following variable name(s) are referenced but never assigned anywhere " +
                "in the script — most likely a typo of a similarly-named variable that IS assigned: " +
                string.Join(", ", unassignedNames.Select(n => $"${n}")) +
                ". Under `set -u` this makes the script fail immediately every time it reaches that reference.");
        }

        if (!shellcheckOk && shellcheckErrors is not null)
        {
            errorParts.Add("shellcheck output:\n" + shellcheckErrors);
        }

        return (false, string.Join("\n\n", errorParts));
    }

    private static async Task<(bool IsValid, string? Errors)> RunShellcheckAsync(string script, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"upgrade-script-{Guid.NewGuid():N}.sh");

        try
        {
            await File.WriteAllTextAsync(tempFile, script, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "shellcheck",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--shell=bash");
            startInfo.ArgumentList.Add("--severity=warning");
            // SC2154 ("referenced but not assigned") — the check most likely to catch a typo'd
            // variable name, exactly the kind of bug that would fail every run under `set -u` — is
            // opt-in, not part of shellcheck's default rule set. Even with it enabled, shellcheck
            // deliberately does not fire SC2154 for a variable guarded by `[ -n "$VAR" ]`/
            // `[ -z "$VAR" ]` (it assumes that idiom means "checking an optional/external
            // variable") — see FindUnassignedVariableReferences for the check that covers exactly
            // that blind spot, which is what actually caught the real bug in testing.
            startInfo.ArgumentList.Add("--enable=all");
            startInfo.ArgumentList.Add(tempFile);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                return (true, null);
            }

            // shellcheck's own usage/parse errors land on stderr; findings land on stdout — either
            // way, strip the temp path so it doesn't leak into a prompt or get persisted anywhere.
            var errors = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (false, errors.Replace(tempFile, "the script", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // shellcheck isn't available or couldn't run — fail open (report valid) rather than
            // silently discarding every generated script over a missing tool.
            return (true, null);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static readonly HashSet<string> WellKnownShellVariables = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "PWD", "OLDPWD", "IFS", "USER", "LOGNAME", "SHELL", "TERM", "TMPDIR",
        "LANG", "LC_ALL", "RANDOM", "SECONDS", "LINENO", "SHLVL", "HOSTNAME", "HOSTTYPE", "OSTYPE",
        "MACHTYPE", "BASH", "BASH_VERSION", "BASHPID", "EUID", "UID", "GROUPS", "PPID", "FUNCNAME",
        "PIPESTATUS", "SUDO_USER", "DISPLAY", "_"
    };

    private static readonly Regex CommentLinePattern = new(@"^[ \t]*#.*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex AssignmentPattern = new(
        @"^\s*(?:export|declare|readonly|typeset|local)?\s*(?:-\S+\s+)*([A-Za-z_][A-Za-z0-9_]*)\s*\+?=",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ForLoopPattern = new(
        @"\bfor\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:in\b|;|\s+do\b)",
        RegexOptions.Compiled);

    private static readonly Regex ReadStatementPattern = new(@"\bread\b[^\n|;&]*", RegexOptions.Compiled);

    private static readonly Regex VariableReferencePattern = new(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>
    /// Best-effort, conservative static check for a variable that's read but never assigned
    /// anywhere in the script — biased toward false negatives (real shell scripting has plenty of
    /// assignment forms this doesn't try to model, e.g. arrays with complex indexing, or a name
    /// assigned only inside a sourced file) over false positives, since a false positive would
    /// waste a retry on — or discard — a perfectly good script. Exists specifically because
    /// shellcheck's own SC2154 check deliberately does not fire for a variable guarded by
    /// `[ -n "$VAR" ]`/`[ -z "$VAR" ]`, on the assumption that pattern means "checking an
    /// optional/external variable" — exactly the pattern that let a real typo ("$APPSrc" for
    /// "$APP_SRC") slip through shellcheck in testing.
    /// </summary>
    private static IReadOnlyList<string> FindUnassignedVariableReferences(string script)
    {
        var withoutComments = CommentLinePattern.Replace(script, "");

        var assigned = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in AssignmentPattern.Matches(withoutComments))
        {
            assigned.Add(match.Groups[1].Value);
        }

        foreach (Match match in ForLoopPattern.Matches(withoutComments))
        {
            assigned.Add(match.Groups[1].Value);
        }

        foreach (Match readMatch in ReadStatementPattern.Matches(withoutComments))
        {
            foreach (var token in readMatch.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token is "read" || token.StartsWith('-'))
                {
                    continue;
                }

                var name = token.TrimEnd(';', ')');
                if (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_'))
                {
                    assigned.Add(name);
                }
            }
        }

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in VariableReferencePattern.Matches(withoutComments))
        {
            referenced.Add(match.Groups[1].Value);
        }

        return referenced
            .Where(name => !assigned.Contains(name) && !WellKnownShellVariables.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Models sometimes wrap output in a markdown code fence despite being told not to —
    /// stripped defensively rather than persisting a script that literally begins with
    /// "```bash".</summary>
    private static string? CleanScriptText(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            trimmed = firstNewline >= 0 ? trimmed[(firstNewline + 1)..] : "";

            var closingFenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFenceIndex >= 0)
            {
                trimmed = trimmed[..closingFenceIndex];
            }

            trimmed = trimmed.Trim();
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private sealed record HostingRepoResult(string Name, string? Url, string? Description, int Stars);

    private class GitHubSearchResponse
    {
        [JsonPropertyName("items")]
        public List<GitHubRepoItem>? Items { get; set; }
    }

    private class GitHubRepoItem
    {
        [JsonPropertyName("full_name")]
        public string? FullName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("stargazers_count")]
        public int StargazersCount { get; set; }
    }

    private class GitLabProjectItem
    {
        [JsonPropertyName("path_with_namespace")]
        public string? PathWithNamespace { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("web_url")]
        public string? WebUrl { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("star_count")]
        public int StarCount { get; set; }
    }

    private class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContentBlock>? Content { get; set; }
    }

    private class AnthropicContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class OpenAiResponse
    {
        [JsonPropertyName("output")]
        public List<OpenAiOutputItem>? Output { get; set; }
    }

    private class OpenAiOutputItem
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public List<OpenAiContentBlock>? Content { get; set; }
    }

    private class OpenAiContentBlock
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }

    private class OllamaChatResponse
    {
        [JsonPropertyName("message")]
        public OllamaMessage? Message { get; set; }
    }

    private class OllamaMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    private class OllamaToolCall
    {
        [JsonPropertyName("function")]
        public OllamaToolCallFunction? Function { get; set; }
    }

    private class OllamaToolCallFunction
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("arguments")]
        public JsonElement Arguments { get; set; }
    }

    private class OllamaWebSearchResponse
    {
        [JsonPropertyName("results")]
        public List<OllamaWebSearchResult>? Results { get; set; }
    }

    private class OllamaWebSearchResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
