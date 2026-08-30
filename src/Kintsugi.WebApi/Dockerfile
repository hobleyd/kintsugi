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

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl shellcheck bash \
    && rm -rf /var/lib/apt/lists/*

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
