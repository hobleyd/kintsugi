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
    && mkdir -p /data/agent-ca-private /data/agent-ca-public /data/agent-packages \
    && chown -R appuser:appuser /data
USER appuser

COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Kintsugi.WebApi.dll"]
