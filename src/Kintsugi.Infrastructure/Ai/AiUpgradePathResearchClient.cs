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
using Kintsugi.Application.UpgradePaths;
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
    private readonly IGitHubSettingsProvider _gitHubSettingsProvider;
    private readonly IGooseCliClient _gooseCliClient;
    private readonly ILogger<AiUpgradePathResearchClient> _logger;

    public AiUpgradePathResearchClient(HttpClient httpClient, IConfiguration configuration, IGitHubSettingsProvider gitHubSettingsProvider, IGooseCliClient gooseCliClient, ILogger<AiUpgradePathResearchClient> logger)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _configuration = configuration;
        _gitHubSettingsProvider = gitHubSettingsProvider;
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

        // bash for macOS, PowerShell for Windows — the same choice BuildScriptGenerationPrompt made
        // when it asked for the script, so the validator can never be checking a script against the
        // wrong language's rules.
        var language = ScriptLanguages.For(request.Platform);

        var (isValid, errors) = await ValidateScriptAsync(script, language, cancellationToken);
        if (isValid)
        {
            _logger.LogInformation("Script generated for {ApplicationName} ({Platform}) passed validation on the first attempt", request.ApplicationName, request.Platform);
            return new UpgradePathScriptResult(UpgradePathStatus.Found, script, null);
        }

        _logger.LogWarning("Script generated for {ApplicationName} ({Platform}) failed validation, retrying once: {Errors}", request.ApplicationName, request.Platform, errors);

        var fixedScript = CleanScriptText(await AskProviderRawAsync(settings, BuildScriptFixPrompt(request, script, errors!), cancellationToken))
            ?? throw new ExternalServiceException("The model's fix attempt did not contain a usable script.");

        var (fixedIsValid, fixedErrors) = await ValidateScriptAsync(fixedScript, language, cancellationToken);
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
    public async Task<string?> CheckScriptVersionAsync(string script, string platform, string applicationName, string applicationIdentifier, CancellationToken cancellationToken)
    {
        // A PowerShell script runs here under pwsh, on this same Linux server — the prompt requires
        // --update-version to make only HTTP calls precisely so a Windows application's version
        // check needs no Windows host to run on. See the runtime image's pwsh install.
        var language = ScriptLanguages.For(platform);
        var tempFile = Path.Combine(Path.GetTempPath(), $"upgrade-check-{Guid.NewGuid():N}{language.FileExtension()}");

        try
        {
            await File.WriteAllTextAsync(tempFile, script, cancellationToken);
            File.SetUnixFileMode(tempFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var startInfo = new ProcessStartInfo
            {
                FileName = language.Interpreter(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (language == ScriptLanguage.PowerShell)
            {
                // -NoProfile so a profile on the server can't inject output into the bare version
                // string this is parsing; -File (rather than -Command) so the script's own
                // `exit <code>` becomes pwsh's exit code, which is what the non-zero check below
                // reads.
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-File");
            }
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

            // From the GitHub settings page, read per call rather than captured — see GitHubSettings.
            // The read-only token, never the script-approval one: this client has no business
            // holding a credential that can write to the approval repository.
            var token = (await _gitHubSettingsProvider.GetAsync(cancellationToken)).ApiToken;
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

        var target = TargetPlatformOf(request.Platform);
        var isWindows = target == TargetPlatform.Windows;

        var identifierLine = string.IsNullOrWhiteSpace(request.ApplicationIdentifier)
            ? ""
            : target switch
            {
                TargetPlatform.Windows =>
                    $"\nApplication identifier (the application's key name under the Windows uninstall registry, e.g. an MSI product code or the vendor's own key): {request.ApplicationIdentifier}",
                TargetPlatform.Linux =>
                    $"\nApplication identifier (whatever the managed host reported for this application, e.g. a Flatpak application ID or a package name): {request.ApplicationIdentifier}",
                _ => $"\nApplication identifier (macOS bundle ID): {request.ApplicationIdentifier}"
            };

        var hostingSection = string.IsNullOrWhiteSpace(hostingSiteContext)
            ? ""
            : $$"""


                Candidate repositories found by searching GitHub and GitLab for this application's
                identifier (a name match doesn't guarantee it's the right project — verify relevance,
                e.g. via its description or README, before relying on it):
                {{hostingSiteContext}}

                """;

        // The platforms differ in three places and nowhere else: what the model is told it's
        // writing (bash or PowerShell), how --update-version has to behave to still run on this
        // Linux server, and what --update is allowed to do on the managed host. Everything around
        // those — the CLI contract, the research instructions, the no-reliable-method sentinel, the
        // "output only the script" rule — is deliberately shared, because the agent, the server-side
        // version check, and the signing flow all treat every platform identically.
        var platformIntro = target switch
        {
            TargetPlatform.Windows => "a Windows application",
            TargetPlatform.Linux => "a Linux application",
            _ => "a macOS application"
        };

        var scriptIntro = target switch
        {
            TargetPlatform.Windows =>
                "Otherwise, write a single PowerShell script implementing this exact CLI contract:\n\n              script.ps1 --appName <name> --appId <id> --update-version\n              script.ps1 --appName <name> --appId <id> --update",
            TargetPlatform.Linux =>
                "Otherwise, write a single bash script implementing this exact CLI contract:\n\n              script.sh --appName <name> --appId <id> --update-version\n              script.sh --appName <name> --appId <id> --update",
            _ =>
                "Otherwise, write a single bash script implementing this exact CLI contract:\n\n              script.sh --appName <name> --appId <bundle-id> --update-version\n              script.sh --appName <name> --appId <bundle-id> --update"
        };

        var updateVersionSection = isWindows
            ? """
              `--update-version` mode — this runs directly on a plain Linux server under PowerShell
              (`pwsh`), NOT on a Windows machine, purely to check for a new release, so it MUST NOT use
              any Windows-only capability (no registry access, no `Get-CimInstance`/WMI, no COM, no
              `winget`, no `Get-Package`, no `[System.Windows.*]`) or touch anything on the filesystem:
              - Determine the current latest stable released version using only `Invoke-RestMethod` /
                `Invoke-WebRequest` and plain text processing. For a GitHub-hosted project, the simplest
                reliable approach is `Invoke-WebRequest -Uri
                'https://github.com/<owner>/<repo>/releases/latest' -MaximumRedirection 0
                -SkipHttpErrorCheck` and reading the last segment of the `Location` response header,
                which names the latest tag with no JSON parsing and no API rate limit at all — prefer
                this kind of redirect trick over parsing a JSON API response. Use your own judgement
                based on where this application is actually distributed.
              - On success, print ONLY the bare version string to stdout (nothing else — no labels, no
                extra lines) and exit 0. Use `[Console]::Out.WriteLine($version)` rather than
                `Write-Host`, so nothing but the version can reach stdout.
              - On failure to determine it, write an error to stderr (`[Console]::Error.WriteLine(...)`)
                and exit non-zero. No stdout output.
              - Must not modify anything, or depend on anything being installed — this mode only checks
                and reports, from a plain Linux `pwsh` with outbound HTTPS available.
              """
            : """
              `--update-version` mode — this runs directly on a plain Linux server, NOT on a Mac,
              purely to check for a new release, so it MUST NOT use any macOS-only tool (no
              `defaults`, `osascript`, `hdiutil`, `plutil`, `installer`, etc.) or touch anything on the
              filesystem:
              - Determine the current latest stable released version using only `curl` and plain text
                processing (`grep`, `sed`, `cut`, `head`, etc. — assume no `jq`). For a GitHub-hosted
                project, the simplest reliable approach is
                `curl -fsS -o /dev/null -w '%{redirect_url}' https://github.com/<owner>/<repo>/releases/latest`,
                which returns a URL ending in the latest tag with no JSON parsing at all — prefer this
                kind of redirect/text trick over parsing a JSON API response. Note the absence of
                `-L`: `%{redirect_url}` reports the redirect curl did NOT follow, so adding `-L` makes
                curl follow it and report an empty string instead. Use your own judgement based on
                where this application is actually distributed.
              - On success, print ONLY the bare version string to stdout (nothing else — no labels, no
                extra lines) and exit 0.
              - On failure to determine it, print an error to stderr and exit non-zero. No stdout output.
              - Must not modify anything, or depend on anything being installed or mounted — this mode
                only checks and reports, from a plain Linux shell with curl available.
              """;

        if (target == TargetPlatform.Linux)
        {
            // Linux is the one platform where this mode runs on the *same kind of system* it is
            // checking for, which makes it the one platform where a plausible-looking script can be
            // quietly, catastrophically wrong: `apt-cache policy` or `rpm -q` here would answer
            // confidently about the API server's own packages, and that answer would then be stored
            // as the latest version for every managed host. The macOS and Windows prompts get this
            // for free — `defaults` and the registry simply don't exist here — so only this one has
            // to say it out loud.
            updateVersionSection = """
                `--update-version` mode — this runs directly on the fleet-management API server, NOT on
                the managed host, purely to check for a new release:
                - CRITICAL: the API server is itself a Linux machine, so a command like `apt-cache
                  policy`, `apt list --upgradable`, `dnf list`, `rpm -q`, `dpkg -l`, `snap info`, or
                  `flatpak remote-info` WILL run here and WILL return an answer — the API server's own
                  answer, about a completely different machine, possibly a different distribution
                  entirely. Never use any of them, and never read anything under `/usr`, `/opt`,
                  `/var/lib/dpkg`, `/var/lib/rpm` or `/etc` to determine a version. This mode must
                  determine the latest version published *by the vendor*, from the network only.
                - Determine the current latest stable released version using only `curl` and plain text
                  processing (`grep`, `sed`, `cut`, `head`, etc. — assume no `jq`). For a GitHub-hosted
                  project, the simplest reliable approach is
                  `curl -fsS -o /dev/null -w '%{redirect_url}' https://github.com/<owner>/<repo>/releases/latest`,
                  which returns a URL ending in the latest tag with no JSON parsing at all — prefer this
                  kind of redirect/text trick over parsing a JSON API response. Note the absence of
                  `-L`: `%{redirect_url}` reports the redirect curl did NOT follow, so adding `-L` makes
                  curl follow it and report an empty string instead. Use your own judgement based on
                  where this application is actually distributed.
                - On success, print ONLY the bare version string to stdout (nothing else — no labels, no
                  extra lines) and exit 0.
                - On failure to determine it, print an error to stderr and exit non-zero. No stdout output.
                - Must not modify anything, or depend on anything being installed — this mode only
                  checks and reports, from a plain Linux shell with curl available.
                """;
        }

        var updateSection = isWindows
            ? """
              `--update` mode — this one DOES run on the managed Windows host itself (as SYSTEM, from a
              service), so Windows tooling is fine here:
              - Re-run the same latest-version check as `--update-version` internally.
              - Determine the currently installed version by reading `DisplayVersion` from the
                application's key under
                `HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<appId>` (also check
                `HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\<appId>` for a
                32-bit application on 64-bit Windows), and verify that key exists before touching
                anything — treat a missing key as a fatal error (wrong app / not installed), not
                something to silently ignore.
              - If already at or above the latest version, print a message and exit 0 without
                downloading or changing anything (idempotent).
              - If the application is currently running, close it gracefully before replacing it —
                already-authorized, so this can be automatic. Use `Get-Process -Name <exe>` then
                `CloseMainWindow()`, poll for the process to actually exit for a bounded grace period
                (e.g. up to 15s, checking every second), and only as a last-resort fallback after that
                grace period use `Stop-Process -Force` if it's still running — never skip straight to a
                hard kill.
              - Download the current release into a directory made under `$env:TEMP` with a unique
                name, removed in a `finally` block covering both success and failure. Prefer a stable
                "latest" URL pattern (e.g. GitHub's `.../releases/latest/download/<asset-filename>`,
                which resolves to whatever is current without needing the version number) over
                constructing a per-version URL from the discovered version string, when the
                distribution channel supports it.
              - For an `.msi`: install via
                `Start-Process msiexec.exe -ArgumentList '/i', $path, '/qn', '/norestart' -Wait -PassThru`
                and check the returned `ExitCode` (0, 1641, and 3010 all mean success; 1641/3010 mean a
                reboot is pending).
              - For an `.exe` installer: use the vendor's own documented silent-install switch
                (`/S`, `/silent`, `/quiet`, `/VERYSILENT /NORESTART` for Inno Setup, `-ms` for
                NSIS-based vendors, etc. — research which one this application's installer actually
                takes rather than guessing), via `Start-Process ... -Wait -PassThru`, and check its
                `ExitCode`.
              - For an `.msix`/`.appx`: install via `Add-AppxPackage`.
              - For any other distribution form, use your best judgement for the equivalent
                non-interactive Windows approach.
              - End by re-reading `DisplayVersion` from the registry and verifying it now meets the
                latest version determined above, and exit non-zero with a clear error on stderr if it
                doesn't.
              """
            : """
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
              """;

        if (target == TargetPlatform.Linux)
        {
            updateSection = """
                `--update` mode — this one DOES run on the managed Linux host itself (as root, from a
                systemd service), so system tooling is fine here:
                - Re-run the same latest-version check as `--update-version` internally.
                - Determine the currently installed version from the application itself where you can
                  (e.g. `<binary> --version`), and verify it really is the application `--appId` names
                  before touching anything — treat a mismatch as a fatal error (wrong app), not
                  something to silently ignore.
                - If already at or above the latest version, print a message and exit 0 without
                  downloading or changing anything (idempotent).
                - Do NOT assume a distribution. Detect which package manager this host actually has —
                  `command -v apt-get`, `dnf`, `zypper`, `pacman`, `apk` — and branch on it, rather
                  than writing a script that only works on Debian derivatives. A script that assumes
                  `apt-get` is one of the most common ways this goes wrong.
                - Prefer the distribution's own package manager when the application is genuinely
                  packaged for it, since that is what will keep working. Otherwise use whatever the
                  vendor actually ships: a `.deb`/`.rpm` downloaded and installed with
                  `apt-get install -y ./<file>` / `dnf install -y ./<file>`, a tarball unpacked under
                  `/opt`, or an AppImage placed somewhere on the PATH.
                - Never run anything interactively. Export `DEBIAN_FRONTEND=noninteractive`, and pass
                  `-y`/`--non-interactive`/`--noconfirm` as the detected manager requires. A script
                  that stops at a debconf prompt will hang until it is killed, mid-transaction.
                - Download into a directory made with `mktemp -d`, cleaned up via a `trap` covering
                  both success and failure. Prefer a stable "latest" URL pattern (e.g. GitHub's
                  `.../releases/latest/download/<asset-filename>`, which resolves to whatever is
                  current without needing the version number) over constructing a per-version URL from
                  the discovered version string, when the distribution channel supports it.
                - If the application is currently running, stop it gracefully before replacing it —
                  already-authorized, so this can be automatic. Send `TERM` (via `systemctl stop` for
                  something that runs as a unit, or `pkill -x`), poll for it to actually exit for a
                  bounded grace period (e.g. up to 15s, checking every second with `pgrep -x`), and
                  only as a last-resort fallback after that use `pkill -9 -x` — never skip straight to
                  a hard kill.
                - End by verifying the installed version now meets the latest version determined above,
                  and exit non-zero with a clear error on stderr if it doesn't.
                """;
        }

        var generalRequirements = isWindows
            ? """
              - Start with `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`.
              - Parse `--appName`, `--appId`, `--update-version`, and `--update` out of `$args`
                yourself — PowerShell's own `param()` binding cannot express double-dashed names, and
                this exact CLI shape is fixed by what invokes the script. `--appName` and `--appId` are
                always both required, along with exactly one of `--update-version` or `--update`.
              - No interactive prompts of any kind anywhere in the script — it always runs unattended,
                as SYSTEM (for `--update`) or on a plain Linux `pwsh` (for `--update-version`). Never
                use `Read-Host`, and always pass whatever silent/quiet/no-restart switches the tools
                you call need.
              - It must pass PSScriptAnalyzer at Warning severity — in particular, use approved
                verb-noun names for any function you define, and don't leave a variable assigned but
                never used.
              """
            : """
              - Start with `#!/bin/bash` and `set -euo pipefail`.
              - `--appName` and `--appId` are always both required, along with exactly one of
                `--update-version` or `--update` (in either order).
              - No interactive prompts of any kind anywhere in the script — it always runs unattended,
                typically as root or an admin user (for `--update`) or on a plain Linux server (for
                `--update-version`).
              """;

        if (target == TargetPlatform.Linux)
        {
            generalRequirements = """
                - Start with `#!/bin/bash` and `set -euo pipefail`.
                - `--appName` and `--appId` are always both required, along with exactly one of
                  `--update-version` or `--update` (in either order).
                - No interactive prompts of any kind anywhere in the script — it always runs unattended,
                  as root from a systemd service (for `--update`) or on the API server (for
                  `--update-version`).
                - It must pass shellcheck cleanly — in particular, quote every expansion, and don't
                  leave a variable assigned but never used.
                """;
        }

        return $$"""
            You are researching how {{platformIntro}} distributes and checks for updates, then
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

            {{scriptIntro}}

            On missing/invalid/conflicting arguments, print a one-line usage message to stderr and
            exit non-zero — no other output.

            {{updateVersionSection}}

            {{updateSection}}

            General requirements:
            {{generalRequirements}}
            - If you have any caveat about your confidence in this script (e.g. you lacked live web
              access, or found conflicting version numbers), say so in a `# WARNING: ...` comment
              near the top rather than in any separate response text — the script is the only thing
              that gets kept.
            - Output ONLY the script itself — no explanation before or after it, and no markdown
              code fences.
            """;
    }

    /// <summary>
    /// Which of the three managed platforms a prompt is being written for. Anything that isn't
    /// recognizably Windows or Linux — including <see cref="PlatformBucket.Generic"/>, the bucket an
    /// unidentifiable operating system string lands in — is treated as macOS, which is what this
    /// prompt builder did for every non-Windows platform before Linux existed as a separate case.
    /// </summary>
    private enum TargetPlatform
    {
        MacOs,
        Windows,
        Linux
    }

    private static TargetPlatform TargetPlatformOf(string platform) => platform switch
    {
        PlatformBucket.Windows => TargetPlatform.Windows,
        PlatformBucket.Linux => TargetPlatform.Linux,
        _ => TargetPlatform.MacOs
    };

    private static string BuildScriptFixPrompt(UpgradePathScriptGenerationRequest request, string script, string validationErrors)
    {
        // Kept in step with BuildScriptGenerationPrompt on purpose: this is the *same* script being
        // repaired, so telling the model something different about where --update-version runs than
        // it was told when writing it is how a repair pass turns a warning into a real bug.
        var target = TargetPlatformOf(request.Platform);
        var isWindows = target == TargetPlatform.Windows;
        var language = isWindows ? "PowerShell" : "bash";
        var fence = isWindows ? "powershell" : "bash";
        var updateVersionConstraint = target switch
        {
            TargetPlatform.Windows =>
                "Remember that --update-version must run correctly on a plain Linux server under `pwsh` with only outbound HTTPS available — no Windows-only capabilities (registry, WMI/CIM, COM, winget) in that mode.",
            TargetPlatform.Linux =>
                "Remember that --update-version runs on the fleet-management API server, not on the managed host — and that the API server is itself a Linux machine, so `apt-cache`/`dnf`/`rpm`/`dpkg`/`snap`/`flatpak` would answer about the *server* rather than failing. That mode must determine the vendor's latest published version over the network with curl, and read nothing from the local filesystem.",
            _ =>
                "Remember that --update-version must run correctly on a plain Linux server with only curl available — no macOS-only tools in that mode."
        };

        return $$"""
            The {{language}} script below, which you wrote as a reusable --update-version/--update tool for
            "{{request.ApplicationName}}" on {{request.Platform}}, has issues found during validation. Fix every one
            of them and return the complete corrected script — don't just patch around the symptom if
            a finding points at a real bug (e.g. a typo'd variable name) or a missing part of the
            required CLI contract (--appName, --appId, --update-version, --update). {{updateVersionConstraint}}

            Original script:
            ```{{fence}}
            {{script}}
            ```

            Validation findings:
            {{validationErrors}}

            Output ONLY the corrected, complete script — no explanation, no markdown code fences.
            """;
    }

    /// <summary>Runs shellcheck against <paramref name="script"/> at warning severity and above —
    /// this is what caught a real bug (a typo'd variable name that would have failed every run
    /// under `set -u`) in testing, which "error"-only severity would have missed, since ShellCheck
    /// classifies most logic bugs as warnings rather than errors. If shellcheck itself can't be
    /// run (not installed, etc.), this fails open — reports valid rather than silently discarding
    /// every generated script over a missing tool.</summary>
    private static readonly string[] RequiredCliContractTokens = { "--appName", "--appId", "--update-version", "--update" };

    private static async Task<(bool IsValid, string? Errors)> ValidateScriptAsync(string script, ScriptLanguage language, CancellationToken cancellationToken)
    {
        // The CLI contract is language-independent — every script, hand-written or AI-authored,
        // bash or PowerShell, is invoked the same way by both the server and the agent.
        var missingCliTokens = RequiredCliContractTokens.Where(token => !script.Contains(token, StringComparison.Ordinal)).ToList();

        // The typo'd-variable check is a bash-specific one: it exists to cover shellcheck's SC2154
        // blind spot under `set -u`, and PowerShell has no equivalent failure mode (an unassigned
        // variable is simply $null unless Set-StrictMode is on, and PSScriptAnalyzer's own
        // PSUseDeclaredVarsMoreThanAssignments covers the analogous case).
        var unassignedNames = language == ScriptLanguage.Bash
            ? FindUnassignedVariableReferences(script)
            : Array.Empty<string>();

        var (analyzerOk, analyzerErrors) = language == ScriptLanguage.PowerShell
            ? await RunScriptAnalyzerAsync(script, cancellationToken)
            : await RunShellcheckAsync(script, cancellationToken);

        if (missingCliTokens.Count == 0 && unassignedNames.Count == 0 && analyzerOk)
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

        if (!analyzerOk && analyzerErrors is not null)
        {
            errorParts.Add((language == ScriptLanguage.PowerShell ? "PSScriptAnalyzer output:\n" : "shellcheck output:\n") + analyzerErrors);
        }

        return (false, string.Join("\n\n", errorParts));
    }

    /// <summary>
    /// The PowerShell counterpart to <see cref="RunShellcheckAsync"/>: parses the script (a syntax
    /// error alone is disqualifying, and PSScriptAnalyzer reports one as a finding rather than
    /// crashing) and runs PSScriptAnalyzer at Warning severity and above, matching shellcheck's own
    /// severity floor — most real logic bugs are classified as warnings, not errors, by both tools.
    /// Fails open the same way for the same reason: a missing <c>pwsh</c>/PSScriptAnalyzer must not
    /// silently discard every generated Windows script.
    /// </summary>
    private static async Task<(bool IsValid, string? Errors)> RunScriptAnalyzerAsync(string script, CancellationToken cancellationToken)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"upgrade-script-{Guid.NewGuid():N}.ps1");

        try
        {
            await File.WriteAllTextAsync(tempFile, script, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            // Written as one -Command expression rather than a script file so there's nothing extra
            // to ship or keep in sync. Exits 1 with the findings on stdout when there are any, so
            // the exit-code contract matches shellcheck's exactly. `-Severity Warning,Error` is what
            // sets the floor. Three rules are excluded: PSAvoidUsingWriteHost, so a legitimate
            // progress message in --update mode isn't treated as a defect; PSAvoidUsingInvokeExpression,
            // which a vendor's own documented install invocation sometimes legitimately needs; and
            // PSUseBOMForUnicodeEncodedFile, which fires on the *temp* file written here rather than
            // on anything that ships — the agent is what decides that encoding, and it always writes
            // a UTF-8 BOM precisely so Windows PowerShell 5.1 decodes a non-ASCII script correctly
            // (see the Windows agent's upgrade.rs).
            startInfo.ArgumentList.Add(
                "$ErrorActionPreference='Stop'; " +
                "$findings = Invoke-ScriptAnalyzer -Path $env:KINTSUGI_SCRIPT_PATH -Severity Warning,Error " +
                "-ExcludeRule PSAvoidUsingWriteHost,PSAvoidUsingInvokeExpression,PSUseBOMForUnicodeEncodedFile; " +
                "if ($findings) { $findings | Format-Table -AutoSize RuleName,Line,Message | Out-String -Width 200; exit 1 } " +
                "else { exit 0 }");
            // Passed by environment rather than interpolated into the -Command string: the path is
            // ours, but interpolating a path into a PowerShell expression is exactly the habit that
            // breaks the first time one contains a quote.
            startInfo.Environment["KINTSUGI_SCRIPT_PATH"] = tempFile;

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

            var errors = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            // PSScriptAnalyzer not being installed surfaces as a CommandNotFoundException on
            // stderr, not as a findings list — that's the fail-open case, not a bad script.
            if (string.IsNullOrWhiteSpace(stdout) && errors.Contains("Invoke-ScriptAnalyzer", StringComparison.Ordinal))
            {
                return (true, null);
            }

            return (false, errors.Replace(tempFile, "the script", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
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
