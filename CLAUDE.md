# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kintsugi is an enterprise patch-management system: an ASP.NET Core 8 backend (`Kintsugi.sln`, Clean
Architecture) plus a Rust macOS agent (`clients/macos-agent/`) that enrolls itself into the fleet,
reports its installed applications, and runs signed upgrade scripts unattended. Upgrade paths for
each application are researched by an AI provider (Anthropic / OpenAI / Ollama / Goose) that
authors the bash script the agent later executes.

Licensing is split: the server is AGPL-3.0, the macOS client is GPL-3.0 — see `LICENSING.md`.

## Commands

Requires the .NET SDK 8 — every csproj targets `net8.0` and `dotnet-ef` is pinned to `8.0.10` with
`rollForward: false`, so a newer SDK alone will not do.

```bash
# Backend
dotnet build Kintsugi.sln
dotnet test tests/Kintsugi.Tests/Kintsugi.Tests.csproj
dotnet test tests/Kintsugi.Tests/Kintsugi.Tests.csproj --filter FullyQualifiedName~HostTests.RecordHeartbeat_SetsStatusAndLastSeenUtc

# EF Core migrations. dotnet-ef is a *local* tool, so restore it first.
dotnet tool restore
dotnet ef migrations add <Name> --project src/Kintsugi.Infrastructure --startup-project src/Kintsugi.WebApi

# Run the whole system (see below — this is the only supported way to run the API)
docker compose up -d --build

# macOS agent (inline #[cfg(test)] modules — checkin_schedule, identity, self_update, ...)
cd clients/macos-agent && cargo build --release
cd clients/macos-agent && cargo test
cd clients/macos-agent && cargo test load_or_assign_persists_a_fresh_minute_when_nothing_is_saved_yet
```

There is no `IDesignTimeDbContextFactory`, and `appsettings.json` deliberately ships **no**
connection string (so no default credential is published), so `dotnet ef` has nothing to resolve
and `Program.cs` throws `InvalidOperationException`. Set `ConnectionStrings__Database` in the
environment when running EF tooling from the host.

**Do not expect `dotnet run` to work.** `Program.cs` hardcodes
`PersistKeysToFileSystem("/data/dataprotection-keys")`, and the connection string is supplied only
by compose as `ConnectionStrings__Database` (pointing at the `db` service); neither exists outside
the container. Run via `docker compose`.
`docker compose build` does not build the tests either — the Dockerfile copies only `src/`.

Releasing the agent: bump `version` in `clients/macos-agent/Cargo.toml`, then run
`packaging/publish-release.sh`, which builds, tars, and POSTs to `/api/agent-packages`.

## Architecture

Layering is conventional (`Domain` ← `Application` ← `Infrastructure` ← `WebApi`) with MediatR
command/query handlers, each feature folder holding a `Command`/`Handler`/`Validator` triad;
FluentValidation runs via `ValidationBehaviour`. What follows is the part no single file explains.

**Agent authentication is two layers, and adding a route needs both.** nginx requires a client
certificate signed by the fleet CA on an *exact-match* regex —
`^/api/(host|applications|patching-policy|upgrade-paths|patch-results|os-patch-results|host-removed)$`
— and forwards the verified Subject CN as `X-Agent-Cert-Cn`. `[RequireAgentIdentity]` then compares
that CN against the `serialNumber` the request body claims (via `IAgentScopedRequest`), so a valid
agent cert can't be used to report data for a different host. **A new agent-facing route is
un-gated until `nginx/default.conf` is edited too** — nothing in the C# will tell you.
`/api/host/enroll` is deliberately outside the regex (an unenrolled agent has no cert yet), as are
the browser-driven `/api/upgrade-paths/...` sub-routes.

**Two separate key hierarchies, kept apart on purpose.** `CaService` mints agent identities;
`ArtifactSigningService` signs script/command *content*. An AI-generated or hand-pasted script
starts **unsigned** — a human must sign it via `POST /api/upgrade-paths/sign-script`, and the agent
verifies against the signing pubkey it pinned at enrollment before executing anything. Do not make
generation sign automatically.

**Razor Pages are not API clients.** `Pages/*.cshtml.cs` inject `ISender` and dispatch the same
MediatR handlers the controllers do.

**Three independent background coordinators** — upgrade-path scan, per-application refresh, and
update-check — each registered twice in `Program.cs`: the concrete type for the hosted service
(which needs writer-side methods) and a narrow interface for Application handlers. Follow that
shape when adding another. Update-check re-runs each resolved script's own `--update-version` mode
and makes no AI call.

**Generated scripts are validated with `shellcheck`**, shelled out to from
`AiUpgradePathResearchClient`. That's why the runtime image installs `shellcheck` and `bash`;
removing them silently degrades script generation to fail-open.

**Fresh deploys redirect everything.** With no `AuthenticationSettings` row saved, all non-`/api`,
non-`/swagger`, non-`/health` traffic redirects to `/settings/authentication`. The OIDC provider is
configured at runtime from the database (`DynamicOpenIdConnectOptionsConfigurator`), not at startup.

## Couplings nothing enforces

- nginx's `default.conf` hardcodes the HTTPS redirect port `8443` and uses a catch-all
  `server_name _`; nginx config gets no environment substitution, so `8443` must be kept in sync
  with `WEB_TLS_PORT` in `.env` by hand. Set a real `server_name` per deployment.
- The installer tarball's top-level entry names are load-bearing: `self_update.rs` extracts
  `kintsugi-agent` by name out of the same archive a human downloads for a fresh install.
- The enrollment token is not baked into published packages — `AgentPackageArchiveRewriter` writes
  the current `AGENT_ENROLLMENT_TOKEN` into `config.toml` on every download, so rotation never
  staleness-breaks a published package. `AgentPackagesController.Download` skips that rewrite for a
  cert-bearing agent, because rewriting would change the bytes and break the publish-time checksum.
- Volumes that must survive a redeploy: `dataprotection-keys` (or every session is signed out),
  `agent-ca-private` / `agent-ca-public` (or the whole fleet must re-enroll), `agent-packages`,
  `db-data`.
- Rust request/response structs mirror C# command/DTO shapes by hand with explicit `serde(rename)`
  — changing a command's JSON shape means changing the matching struct in `clients/macos-agent/`.

## Conventions

Comments here explain *why* a decision was made and name the file at the other end of a coupling
(C# doc comments referencing `checkin_schedule.rs`, Rust comments referencing
`EnrollAgentCommandHandler`). Match that density and that habit — the cross-references are how this
codebase stays navigable. Domain entities use private setters with static factory methods and
behaviour methods; keep invariants in `Domain`, not in handlers.
