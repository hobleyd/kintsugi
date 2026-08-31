# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kintsugi is an enterprise patch-management system: an ASP.NET Core 8 backend (`Kintsugi.sln`, Clean
Architecture) plus three Rust agents — `clients/macos-agent/`, `clients/windows-agent/` and
`clients/linux-agent/` — that enroll themselves into the fleet, report their installed applications,
and run signed upgrade scripts unattended. Upgrade paths for each application are researched by an AI
provider (Anthropic / OpenAI / Ollama / Goose) that authors the script the agent later executes:
**bash for macOS and Linux, PowerShell for Windows**.

This directory is a git repository; `origin` is `git@github.com:hobleyd/kintsugi.git`, and it is
**public**. So no real deployment detail belongs in a tracked file — not just credentials but the
server's own address. Secrets and TLS material stay in `.env` and `nginx/tls/`, both gitignored;
every agent's `DEFAULT_API_BASE_URL` and `packaging/config.toml` ships the placeholder
`kintsugi.example.com`, because a real fleet sets its address at install time via `config.toml` or
`PATCHING_AGENT_API_BASE_URL` and never by editing that default. Keep the three agents' defaults
identical — each one's comment claims it is in step with the others.

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

# Linux agent — same shape again. Builds and tests natively on Linux; from macOS, run it in a
# container (see below), which is a real Linux build rather than a cross-compile.
cd clients/linux-agent && cargo build --release
cd clients/linux-agent && cargo test
```

There is no `IDesignTimeDbContextFactory`, so `dotnet ef` resolves the connection string from
`appsettings.json`, whose value is `Host=db` — only resolvable inside compose. Override
`ConnectionStrings__Database` when running EF tooling from the host.

**Do not expect `dotnet run` to work.** `Program.cs` hardcodes
`PersistKeysToFileSystem("/data/dataprotection-keys")` and the default connection string points at
the `db` service; both only exist inside the container. Run via `docker compose`.
`docker compose build` does not build the tests either — the Dockerfile copies only `src/`.

Releasing an agent: bump `version` in that agent's `Cargo.toml` and merge to `main`. CI
(`.github/workflows/ci.yml`) runs every test suite, then builds and tags a GitHub Release per agent
whose version isn't already released — `macos-agent-v0.5.0` and so on, one `.tar.gz` asset each.
It never POSTs to a server; the server pulls, via the Clients page's "Refresh clients" (below).

Each publish script still works by hand and is still the only place the archive's layout is
defined; CI calls the same script with `--binary`/`--output-dir` (`-Binary`/`-OutputDir` on
Windows) so the `tar` invocation is never duplicated in YAML. Run one directly and it builds and
POSTs to `/api/agent-packages` as before. The Linux one must then be run on the *oldest* glibc in
the fleet (or a container of it): a binary linked against a newer glibc will not start on an older
host, and nothing in the publish path checks that. **CI sidesteps that entirely** by targeting
`x86_64-unknown-linux-musl` — statically linked, so there is no libc floor at all, which is only
possible because the agent links no C library of its own (rustls links nothing; `ksni` speaks
StatusNotifierItem over zbus rather than linking GTK). It asserts the result is not dynamically
linked rather than trusting the target name. macOS gets a `lipo`'d universal binary for the same
reason there is one package per platform: an arm64-only build would silently exclude every Intel
Mac in the fleet.

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

**Working on the Linux agent from a non-Linux machine.** Far easier, because there is no
cross-compilation involved at all — a Linux container *is* the target:

```bash
docker run --rm -v "$PWD/clients/linux-agent":/w -w /w rust:1-slim cargo test
```

It links no C library and no GUI toolkit (see the `ksni` note in its `Cargo.toml`), so the stock
image needs nothing added. Every output-parsing function — `flatpak list`, `snap list`, the DMI
serial screening, `apt-get --just-print upgrade` and the four other package managers' listings —
takes a `&str` and has tests against captured real output, for exactly the reason the Windows
`winget list` parser does.

**Verifying a server-written upgrade script actually works.** `dotnet test` only asserts the shape
of the text; it never runs it. The scripts' `--update-version` mode is a few lines of `curl` against
a public catalog, so running it the way `CheckScriptVersionAsync` does costs seconds and is the only
thing that catches a script that is syntactically perfect and answers nothing:

```bash
docker run --rm -v "$PWD/scripts":/w debian:12-slim \
    sh -c 'apt-get update -qq && apt-get install -y -qq curl && bash /w/flatpak.sh --appName Firefox --appId org.mozilla.firefox --update-version'
```

This is what caught `curl -fsSL -o /dev/null -w '%{redirect_url}'` returning an empty string — `-L`
makes curl *follow* the redirect, so the variable reporting the un-followed redirect is empty. That
one had shipped in Homebrew's own self-update row and in the prompt text recommending the pattern to
the AI; nothing surfaced it, because the failure is a null `LatestVersion`, which is indistinguishable
from "no update available".

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
maps a bucket to bash (macOS, Linux, Homebrew, Flatpak, Snap) or PowerShell (Windows, winget,
Chocolatey), and that one function governs three things that must never
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
use whatever the platform provides. All three prompts say so explicitly; keep them saying it.

Linux is the dangerous one here, and its prompt says so at length. On the other two platforms a
host-local version check simply *fails* on the API server — `defaults` and the registry aren't there.
On Linux the API server is the same kind of machine as the managed host, so `apt-cache policy`,
`rpm -q`, `snap info` and friends all run happily and return the *server's* answer about a
completely different machine. That answer is then stored as `LatestVersion` for every host sharing
the row. Nothing errors.

**Client builds come off GitHub, and the server configures them on the way in.** CI cannot publish
to a Kintsugi server — it has no route to one, and a server's address is deployment detail that
must never be committed — so the released archives carry the `kintsugi.example.com` placeholder and
the direction is reversed. The Clients page checks the repository's releases on every load and
"Refresh clients" downloads what's newer, rewrites `api_base_url` to this server's own address, and
republishes it locally (`ImportAgentPackagesFromSourceCommandHandler`). That address comes from
`AGENT_API_BASE_URL`, falling back to the address the page was reached on when it is unset.

**The fallback is a guess, and the admin UI's address is frequently the wrong answer.** nginx is
what verifies the agent's client certificate, so anything terminating TLS in front of it — a
gateway, a load balancer, a CDN — ends the mutual-TLS handshake at itself and cannot pass the
certificate on. `AGENT_API_BASE_URL` must name **nginx's own address and `WEB_TLS_PORT`**. Getting
it wrong fails in the quietest way the system has: `/api/host/enroll` is deliberately outside
nginx's client-certificate regex, so the agent enrolls, looks installed, and then 403s on every
authenticated route forever. That is not hypothetical — it shipped, from an earlier version of this
page that derived the address unconditionally and argued it was safe because the plain-HTTP
listener only 301s to the TLS one. That argument covers the scheme and the port and misses the
front door. The page now says out loud when it is falling back.

A deployment where something else already owns 443 therefore needs agents routed to nginx *without*
that hop terminating them, which is what `nginx/edge-sni-router.conf.example` documents: an
`ssl_preread` stream server that reads the SNI hostname off the ClientHello and hands the agent
hostname's bytes through untouched. It is the only shape that works, because a mutual-TLS handshake
can only be verified by whatever terminates it. Note the CDN case specifically — a proxying CDN's
own mTLS feature validates against *its* CA and forwards the verdict in a header, which is not what
`$ssl_client_verify` reads, so the agent hostname has to bypass the CDN's proxy entirely.

That rewrite happens at **import**, not download, and the two rewrites `IAgentPackageArchiveRewriter`
performs are deliberately split that way: `api_base_url` is baked into the stored bytes so the
checksum signed over them already describes this server and an enrolled agent's byte-identical
self-update download still verifies, while `enrollment_token` is substituted per download because it
rotates far more often than a build does. Refresh is a **Razor Page handler, not an API route** —
`location ^~ /api/agent-packages` is a prefix match with no client certificate required and
`Program.cs` exempts all of `/api` from the sign-in gate, so an API route would be triggerable by
anyone who can reach the server. This is the rare case where `nginx/default.conf` correctly needs
no edit.

**Fresh deploys redirect everything.** With no `AuthenticationSettings` row saved, all non-`/api`,
non-`/swagger`, non-`/health` traffic redirects to `/settings/authentication`. The OIDC provider is
configured at runtime from the database (`DynamicOpenIdConnectOptionsConfigurator`), not at startup.

## Platform buckets, and why package managers get their own

`PlatformBucket` keys an `upgrade_paths` row. An AI-researched row lives under an *OS* bucket
(`macOS`, `Windows`, `Linux`); a package-manager-managed row lives under its *manager's* bucket
(`pm:Homebrew`, `pm:winget`, `pm:Chocolatey`, `pm:Flatpak`, `pm:Snap` — see
`PlatformBucket.ForPackageManager`), because what a `brew upgrade` row actually depends on is the
manager, not the OS.

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

**There is a hard entry requirement for that catalog, and it is not "an agent can drive it".** A
manager belongs there only if its catalog can be queried *over HTTP from the API server*, because
that is where `--update-version` runs and because one row per (application, manager) is shared by the
whole fleet. Homebrew, winget, Chocolatey, Flathub and the Snap Store each publish one global
catalog and satisfy both. **apt, dnf, zypper and pacman satisfy neither** — "the latest version of
curl" depends on which repositories *that* host has configured, and one `pm:APT` row would have
Debian 12 and Ubuntu 24.04 overwriting each other's answer forever. So they are deliberately absent,
and the Linux agent reports what they manage as *OS updates* instead: `apt`/`dnf` is to Linux what
`softwareupdate` is to macOS — it patches the operating system and everything the vendor ships with
it. That is why the Linux inventory lists only Flatpak and Snap applications and never dpkg/rpm
packages, and it is not a gap. See its `os_update` and `main::collect_installed_applications`.

Every `*UpgradeScript.Build` must return **byte-identical content for every application** — the
name and id are read from `--appName`/`--appId` at runtime, never baked in. That is what lets one
human "Sign Script" review cover every application a manager handles, via
`FindExistingSignatureForScriptAsync`.

## The three agents

They are deliberately the same program in different clothes: same modules, same names, same
ordering, same comments where the reasoning carries over. Read the macOS one first — it's the
original — then the others for what each platform forced to differ. The differences that matter:

| | macOS | Windows | Linux |
|---|---|---|---|
| Privileged half | root LaunchDaemon, re-invoked by launchd | resident service (`windows-service`) | systemd oneshot on a `.timer` |
| Per-user half | LaunchAgent | logon-triggered task for `BUILTIN\Users` | systemd user unit, `graphical-session.target` |
| Check-in schedule | rewrites its own plist, reloads launchd via a detached helper | computes its next wake in-process | rewrites its own `.timer`, `daemon-reload` |
| Privilege handoff | queue, OS updates only | queue, everything | queue, everything |
| Inventory | `/Applications` bundles + Homebrew | uninstall registry (3 views) + winget + Chocolatey | Flatpak + Snap (not dpkg/rpm — see above) |
| OS updates | `softwareupdate` | Windows Update Agent COM API, via PowerShell | apt / dnf / yum / zypper / pacman / apk |
| Host identity | hardware serial, always present | SMBIOS serial, **often a placeholder** | DMI serial, **often a placeholder** |
| Nobody logged in | nothing patches | nothing patches | root service patches unattended — see below |

**Linux borrows its architecture from Windows, not macOS, and for the same forcing reason.** Every
upgrade it can perform (`apt-get`, `dnf`, `flatpak update --system`, `snap refresh`) requires root,
so patching lives in the root service and the per-user process holds no identity and makes no
authenticated call — it decides *when*, and asks. macOS is the odd one out precisely because
Homebrew *refuses* to run as root and installs into a user-writable prefix. The queue directory is
`root:root 1733` (a drop-box: anyone may write, only root may read or list), which is the Linux
spelling of the macOS queue's `root:admin 0770` and needs no group — "local administrators" is
`sudo` on Debian, `wheel` on Red Hat, and neither elsewhere.

**Only the Linux agent patches with nobody logged in, and it has to.** Both other agents put the
patching schedule in the per-user process, which costs nothing when every managed host is somebody's
desktop. Most of a Linux fleet is servers with no graphical session at all, so the same design would
mean the majority of hosts silently never patched. The per-user process writes a heartbeat into the
queue directory (`queue::record_heartbeat`); when the root service's hourly check-in finds none
recent, it runs the cycle itself with the confirm/delay/warning steps dropped rather than faked —
there is nobody to ask. The per-user process exits immediately when it has no `DISPLAY`, so an SSH
login can't suppress a server's own patching by leaving a heartbeat behind.

**Two root entry points mean an explicit lock.** launchd and the Windows SCM both give mutual
exclusion for free (one instance per job; one resident service). systemd guarantees that per *unit*,
and the Linux agent has two — `kintsugi-agent.service` on the timer and `kintsugi-agent-queue.service`
on a `.path` watch — so `lock.rs` takes an advisory `flock` both of them hold. Without it a
queue-triggered patch can land inside an unattended cycle and two `apt-get` runs deadlock on the
dpkg lock with no useful error.

**The Windows tray process holds no identity and makes no network call.** On macOS the per-user
process talks to the server directly and runs patches itself (Homebrew refuses to run as root). On
Windows every upgrade needs elevation, so patches move to the service anyway — and once they have,
the tray process has no reason to hold the client private key either. So it goes through
`queue.rs` for all three privileged things: *what's pending*, *patch this application*, *install
Windows updates*. The security property is the macOS queue's, strengthened: **a request never
carries anything executable.** An app-patch request names an application; the service independently
re-fetches that application's upgrade path from the server and verifies its signature before running
anything. The worst a forged request can do is start an already-approved upgrade early.

**Windows and Linux serial numbers are frequently placeholders.** `Win32_BIOS.SerialNumber` and
`/sys/class/dmi/id/product_serial` read the same SMBIOS field and inherit the same junk from board
vendors: "To Be Filled By O.E.M.", "Default string", "0", "Not Specified" (which is what every guest
of a bare `qemu-system-x86_64` reports). The serial *is* this host's identity — it becomes the
certificate CN, which `[RequireAgentIdentity]` compares against every request body — so two hosts
sharing one would share a host record, a certificate, and each other's data.
`system_info::serial_number` in both agents therefore screens against a placeholder list, falls back
to a per-installation id (the Windows `MachineGuid`, systemd's `/etc/machine-id`), and **refuses to
enroll** rather than inventing a value. macOS has no equivalent failure mode.

**Replacing a running binary differs.** macOS and Linux stage next to the target and rename over it
(atomic, and Unix will unlink an open file). Windows locks a running image, so `self_update` renames
the *old* binary aside — which Windows does allow — copies the new one into the freed path, and
deletes the displaced copy at next service start. It restores the old one if the copy fails; leaving
the path empty would break the agent permanently. Linux also has nothing to restart on the root
side: it is a oneshot that is about to exit, and the next timer firing execs whatever is at the path
by then — only the long-running per-user units get restarted.

## Couplings nothing enforces

- nginx's `default.conf` hardcodes the HTTPS redirect port `8443` (both server blocks match any
  host — `server_name _`); nginx config gets no environment substitution, so `8443` must be kept
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
- nginx's own server certificate (`nginx/tls/fullchain.pem`) is what every agent validates, via
  `rustls-tls-native-roots` — i.e. against the *host OS* trust store, with no way to pin or except
  anything. A self-signed certificate there is rejected at the handshake, so the whole fleet stops
  checking in at once and the agent log calls it a connection failure. Two consequences: the file
  must hold a publicly-trusted chain (leaf **plus** intermediates — nothing downstream completes it
  now), and if a proxy in front used to own renewal, it no longer does. Whoever renews has to copy
  the new pair to this host and reload nginx, on a cadence shorter than the certificate's life, or
  the fleet goes dark on expiry day with no warning and a symptom that reads like a network outage.
- CI's release tags (`<platform>-agent-v<version>`) are parsed by `GitHubAgentPackageSourceClient`
  to work out which platform and version a release is. Renaming a tag on either side silently stops
  that platform ever being found again — a refresh just reports nothing new.
- The agent-package platform namespace (`"macos"`, `"windows"`, `"linux"`) is *not*
  `PlatformBucket`'s namespace (`"macOS"`, `"Windows"`, `"Linux"`, `"pm:..."`). They name different
  things; don't unify them.
- `PackageManagerCatalog`'s names are the strings agents report in `InstalledApp.package_manager`.
  A rename on either side silently stops an entire manager's applications resolving.
- A package-manager row is only patchable if the *agent* reported an `applicationIdentifier` for that
  installed application — `is_patchable` requires one for any `Script` row, and it comes from the
  `InstalledApplication`, not from the `UpgradePath` (which always has one, falling back to the
  name). The Windows and Linux agents set it for every managed package; the macOS agent leaves it
  unset for Homebrew formulae/casks, which is why those rows do not currently patch.
- Volumes that must survive a redeploy: `dataprotection-keys` (or every session is signed out),
  `agent-ca-private` / `agent-ca-public` (or the whole fleet must re-enroll), `agent-packages`,
  `db-data`.
- Rust request/response structs mirror C# command/DTO shapes by hand with explicit `serde(rename)`
  — changing a command's JSON shape means changing the matching struct in **all three** agents.
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
