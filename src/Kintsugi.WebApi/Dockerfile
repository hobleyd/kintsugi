FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Kintsugi.sln ./
COPY src/Kintsugi.Domain/Kintsugi.Domain.csproj src/Kintsugi.Domain/
COPY src/Kintsugi.Application/Kintsugi.Application.csproj src/Kintsugi.Application/
COPY src/Kintsugi.Infrastructure/Kintsugi.Infrastructure.csproj src/Kintsugi.Infrastructure/
COPY src/Kintsugi.WebApi/Kintsugi.WebApi.csproj src/Kintsugi.WebApi/
RUN dotnet restore src/Kintsugi.WebApi/Kintsugi.WebApi.csproj

COPY src/ src/
RUN dotnet publish src/Kintsugi.WebApi/Kintsugi.WebApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# shellcheck + bash validate and run the bash upgrade scripts (macOS hosts, Homebrew); pwsh +
# PSScriptAnalyzer do the same for the PowerShell ones (Windows hosts, winget/Chocolatey). Both
# pairs are load-bearing, not optional tooling: AiUpgradePathResearchClient fails *open* when a
# validator is missing, so removing one silently degrades script generation to unchecked, and
# CheckScriptVersionAsync needs the matching interpreter present to learn any application's latest
# version at all — without which UpgradePath.LatestVersion stays null, updateAvailable stays false,
# and no agent on that platform ever patches anything. See ScriptLanguages.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl shellcheck bash ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# PowerShell isn't in Debian's own archives — installed from Microsoft's package feed, whose
# component path tracks the base image's Debian release.
RUN apt-get update \
    && apt-get install -y --no-install-recommends gnupg \
    && curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && rm /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends powershell \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# The Claude Agent SDK provider (AiProvider.ClaudeAgentSdk) drives Claude by running this binary
# as a subprocess — the SDK's documented interface for a host language it publishes no library for,
# and there is no C# one. Installed from Anthropic's own signed apt repository for the same reason
# PowerShell comes from Microsoft's feed rather than being vendored: the package manager owns the
# key check and the upgrade path. The `stable` channel is deliberate — it lags about a week and
# skips releases with known regressions, and package-manager installs never auto-update themselves,
# so the version answering research runs only ever changes when this image is rebuilt.
#
# Unlike shellcheck and pwsh, this one is *not* load-bearing for every deployment: an operator who
# never selects the Claude Agent SDK provider will never invoke it. Removing it degrades honestly —
# the Settings status check says the command is not installed — rather than silently.
RUN apt-get update \
    && apt-get install -y --no-install-recommends gnupg \
    && install -d -m 0755 /etc/apt/keyrings \
    && curl -fsSL https://downloads.claude.ai/keys/claude-code.asc -o /etc/apt/keyrings/claude-code.asc \
    # The published fingerprint. apt would accept any key placed at that path, so it is checked
    # here rather than trusted because the download succeeded.
    && gpg --show-keys --with-colons /etc/apt/keyrings/claude-code.asc \
        | grep -q "^fpr:::::::::31DDDE24DDFAB679F42D7BD2BAA929FF1A7ECACE:" \
    && echo "deb [signed-by=/etc/apt/keyrings/claude-code.asc] https://downloads.claude.ai/claude-code/apt/stable stable main" \
        > /etc/apt/sources.list.d/claude-code.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends claude-code \
    && apt-get purge -y gnupg \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# Installed to the all-users module path (rather than a user profile) so it resolves for appuser,
# who this image drops to below and who has no writable home of their own.
RUN pwsh -NoProfile -NonInteractive -Command \
    "Set-PSRepository -Name PSGallery -InstallationPolicy Trusted; \
     Install-Module -Name PSScriptAnalyzer -Scope AllUsers -Force -AcceptLicense"

RUN adduser --disabled-password --gecos "" --uid 5678 appuser \
    # CaService/ArtifactSigningService's persistent home (see docker-compose.yml's
    # agent-ca-private/agent-ca-public volumes) — pre-created and owned by appuser here so that,
    # on a fresh volume, Docker's copy-up from the image gives it correct ownership immediately,
    # rather than appuser hitting Access Denied trying to write the CA it's supposed to generate.
    # /data/claude-code is the CLI's config directory and /data/claude-code/workspace the empty
    # directory it is run from. Both are pre-created and owned by appuser because the CLI writes
    # to its config directory on every run, and because — without `--bare`, which cannot be used
    # here since bare mode ignores CLAUDE_CODE_OAUTH_TOKEN entirely — it reads `.claude/`,
    # `.mcp.json` and `CLAUDE.md` out of whatever directory it starts in. Neither needs to survive
    # a redeploy, so neither is a compose volume. See ClaudeAgentSdkClient.
    && mkdir -p /data/agent-ca-private /data/agent-ca-public /data/agent-packages \
        /data/claude-code/workspace \
    && chown -R appuser:appuser /data
USER appuser

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
# Matched by ClaudeAgentSdkClient, which also sets these on the child process — they are here so
# that anyone running `claude` inside the container by hand to diagnose a problem sees the same
# configuration the application does.
ENV CLAUDE_CONFIG_DIR=/data/claude-code
ENV DISABLE_AUTOUPDATER=1
EXPOSE 8080

ENTRYPOINT ["dotnet", "Kintsugi.WebApi.dll"]
