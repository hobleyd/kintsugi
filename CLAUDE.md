# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kintsugi is an enterprise patch-management system: an ASP.NET Core 8 backend (`Kintsugi.sln`, Clean
Architecture) plus two Rust agents — `clients/macos-agent/` and `clients/windows-agent/` — that
enroll themselves into the fleet, report their installed applications, and run signed upgrade
scripts unattended. Upgrade paths for each application are researched by an AI provider (Anthropic /
OpenAI / Ollama / Goose) that authors the script the agent later executes: **bash for macOS,
PowerShell for Windows**.

This directory is **not** a git repository.

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

# Windows agent — same shape, but it only builds on Windows (winreg, windows-sys, windows-service)
cd clients/windows-agent && cargo build --release
cd clients/windows-agent && cargo test
```

There is no `IDesignTimeDbContextFactory`, so `dotnet ef` resolves the connection string from
`appsettings.json`, whose value is `Host=db` — only resolvable inside compose. Override
`ConnectionStrings__Database` when running EF tooling from the host.

**Do not expect `dotnet run` to work.** `Program.cs` hardcodes
`PersistKeysToFileSystem("/data/dataprotection-keys")` and the default connection string points at
the `db` service; both only exist inside the container. Run via `docker compose`.
`docker compose build` does not build the tests either — the Dockerfile copies only `src/`.

Releasing an agent: bump `version` in that agent's `Cargo.toml`, then run its own publish script —
`packaging/publish-release.sh` (macOS) or `packaging/publish-release.ps1` (Windows), both of which
build, tar, and POST to `/api/agent-packages`.

**Working on the Windows agent from a non-Windows machine.** It can't be built natively, but it can
be fully type-checked *and its unit tests actually run*, via Docker + mingw + Wine:

```bash
docker run --rm --platform linux/amd64 -v "$PWD/clients/windows-agent":/w -w /w <image> \
    cargo test --target x86_64-pc-windows-gnu
```

where `<image>` is `rust:1-slim` plus `gcc-mingw-w64-x86-64` and `wine`, with
`CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER=x86_64-w64-mingw32-gcc` and
`CARGO_TARGET_X86_64_PC_WINDOWS_GNU_RUNNER=wine`. The `-gnu` target rather than `-msvc` because
`ring`'s build script compiles C and the MSVC headers can't be shipped; both exercise the same
`#[cfg(windows)]` code and the same `windows-sys` bindings. This is worth the setup — it is what
caught the `winget list` parser silently reporting zero applications.

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

**Every upgrade script is one of two languages, decided by its platform bucket.** `ScriptLanguages.For`
maps a bucket to bash or PowerShell, and that one function governs three things that must never
disagree: which prompt `BuildScriptGenerationPrompt` writes, which validator checks the result
(`shellcheck` vs `Invoke-ScriptAnalyzer`), and which interpreter `CheckScriptVersionAsync` runs it
under (`bash` vs `pwsh`). That's why the runtime image installs all four; removing any of them
silently degrades generation to fail-open, or leaves `LatestVersion` null — and a null
`LatestVersion` means `updateAvailable` is false, which means the agent's `is_patchable` returns
false, which means **nothing on that platform ever patches**.

**`--update-version` always runs on the Linux API server; `--update` always runs on the managed
host.** This split is the whole reason a durable script is generated at all — checking for a new
release costs a subprocess, not an AI call. So `--update-version` may only make HTTP calls (no
`defaults`/`hdiutil` on macOS, no registry/WMI/COM/winget on Windows), while `--update` is free to
use whatever the platform provides. Both prompts say so explicitly; keep them saying it.

**Fresh deploys redirect everything.** With no `AuthenticationSettings` row saved, all non-`/api`,
non-`/swagger`, non-`/health` traffic redirects to `/settings/authentication`. The OIDC provider is
configured at runtime from the database (`DynamicOpenIdConnectOptionsConfigurator`), not at startup.

## Platform buckets, and why package managers get their own

`PlatformBucket` keys an `upgrade_paths` row. An AI-researched row lives under an *OS* bucket
(`macOS`, `Windows`); a package-manager-managed row lives under its *manager's* bucket
(`pm:Homebrew`, `pm:winget`, `pm:Chocolatey` — see `PlatformBucket.ForPackageManager`), because what
a `brew upgrade` row actually depends on is the manager, not the OS.

That used to be one shared `generic` bucket, which was safe only while Homebrew was the sole package
manager: `UpgradePathRepository`'s lookup falls back to it for *any* host, so a Windows host with an
application whose name matched a Homebrew formula would have been handed a signed `#!/bin/bash`
script — and, the signature being genuine, its agent would have run it. The fallback is now to the
bucket of whichever manager owns that installation, resolved from the installed application's
parent. `SplitPackageManagerPlatformBucket` migrates the pre-existing `generic` rows in place rather
than deleting them, specifically to preserve their `ScriptSignature` (a human's review).

Adding a package manager means one entry in `PackageManagerCatalog` plus a `*UpgradeScript` builder.
The catalog is what both `ResearchApplicationUpgradePathCommandHandler` and
`RegisterApplicationsCommandHandler` recognize managers by, so they can't drift apart.

Every `*UpgradeScript.Build` must return **byte-identical content for every application** — the
name and id are read from `--appName`/`--appId` at runtime, never baked in. That is what lets one
human "Sign Script" review cover every application a manager handles, via
`FindExistingSignatureForScriptAsync`.

## The two agents

They are deliberately the same program in different clothes: same modules, same names, same
ordering, same comments where the reasoning carries over. Read the macOS one first — it's the
original — then the Windows one for what the platform forced to differ. The differences that matter:

| | macOS | Windows |
|---|---|---|
| Privileged half | root LaunchDaemon, re-invoked by launchd | resident service (`windows-service`) |
| Per-user half | LaunchAgent | logon-triggered task for `BUILTIN\Users` |
| Check-in schedule | rewrites its own plist, reloads launchd via a detached helper | computes its next wake in-process |
| Inventory | `/Applications` bundles + Homebrew | uninstall registry (3 views) + winget + Chocolatey |
| OS updates | `softwareupdate` | Windows Update Agent COM API, via PowerShell |
| Host identity | hardware serial, always present | SMBIOS serial, **often a placeholder** — see below |

**The Windows tray process holds no identity and makes no network call.** On macOS the per-user
process talks to the server directly and runs patches itself (Homebrew refuses to run as root). On
Windows every upgrade needs elevation, so patches move to the service anyway — and once they have,
the tray process has no reason to hold the client private key either. So it goes through
`queue.rs` for all three privileged things: *what's pending*, *patch this application*, *install
Windows updates*. The security property is the macOS queue's, strengthened: **a request never
carries anything executable.** An app-patch request names an application; the service independently
re-fetches that application's upgrade path from the server and verifies its signature before running
anything. The worst a forged request can do is start an already-approved upgrade early.

**Windows serial numbers are frequently placeholders.** `Win32_BIOS.SerialNumber` ships as "To Be
Filled By O.E.M.", "Default string", "0", and so on. The serial *is* this host's identity — it
becomes the certificate CN, which `[RequireAgentIdentity]` compares against every request body — so
two hosts sharing one would share a host record, a certificate, and each other's data.
`system_info::serial_number` therefore screens against a placeholder list, falls back to the
Windows `MachineGuid`, and **refuses to enroll** rather than inventing a value. macOS has no
equivalent failure mode.

**Replacing a running binary differs.** macOS stages next to the target and renames over it (atomic,
and Unix will unlink an open file). Windows locks a running image, so `self_update` renames the
*old* binary aside — which Windows does allow — copies the new one into the freed path, and deletes
the displaced copy at next service start. It restores the old one if the copy fails; leaving the
path empty would break the agent permanently.

## Couplings nothing enforces

- nginx's `default.conf` hardcodes the HTTPS redirect port `8443` and `server_name
  kintsugi.example.com`; nginx config gets no environment substitution, so `8443` must be kept
  in sync with `WEB_TLS_PORT` in `.env` by hand.
- The installer tarball's top-level entry names are load-bearing: `self_update.rs` extracts
  `kintsugi-agent` / `kintsugi-agent.exe` by name out of the same archive a human downloads for a
  fresh install. Both agents publish `.tar.gz` — Windows included — because
  `AgentPackageArchiveRewriter` reads gzip-tar specifically, and `tar.exe` has shipped in Windows
  since 10 1803.
- The enrollment token is not baked into published packages — `AgentPackageArchiveRewriter` writes
  the current `AGENT_ENROLLMENT_TOKEN` into `config.toml` on every download, so rotation never
  staleness-breaks a published package. `AgentPackagesController.Download` skips that rewrite for a
  cert-bearing agent, because rewriting would change the bytes and break the publish-time checksum.
- The agent-package platform namespace (`"macos"`, `"windows"`) is *not* `PlatformBucket`'s
  namespace (`"macOS"`, `"Windows"`, `"pm:..."`). They name different things; don't unify them.
- `PackageManagerCatalog`'s names are the strings agents report in `InstalledApp.package_manager`.
  A rename on either side silently stops an entire manager's applications resolving.
- Volumes that must survive a redeploy: `dataprotection-keys` (or every session is signed out),
  `agent-ca-private` / `agent-ca-public` (or the whole fleet must re-enroll), `agent-packages`,
  `db-data`.
- Rust request/response structs mirror C# command/DTO shapes by hand with explicit `serde(rename)`
  — changing a command's JSON shape means changing the matching struct in **both** agents.
- Windows PowerShell 5.1 decodes a BOM-less `.ps1` using the system ANSI code page, not UTF-8. The
  Windows agent writes every script with a UTF-8 BOM for exactly that reason, and the
  server-written ones are kept ASCII-only as well.

## Conventions

Comments here explain *why* a decision was made and name the file at the other end of a coupling
(C# doc comments referencing `checkin_schedule.rs`, Rust comments referencing
`EnrollAgentCommandHandler`, each agent's comments naming the other where they diverge). Match that
density and that habit — the cross-references are how this codebase stays navigable. Domain entities
use private setters with static factory methods and behaviour methods; keep invariants in `Domain`,
not in handlers.
