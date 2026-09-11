# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Kintsugi is an enterprise patch-management system: an ASP.NET Core 8 backend (`Kintsugi.sln`, Clean
Architecture), a Flutter web admin UI (`web/`, Clean Architecture + BLoC, served as static files by
the nginx container), plus three Rust agents — `clients/macos-agent/`, `clients/windows-agent/` and
`clients/linux-agent/` — that enroll themselves into the fleet, report their installed applications,
and run signed upgrade scripts unattended. Upgrade paths for each application are researched by an AI
provider (Anthropic API / OpenAI / Ollama / Goose / Claude Agent SDK) that authors the script the
agent later executes: **bash for macOS and Linux, PowerShell for Windows**.

This directory is a git repository; `origin` is `git@github.com:hobleyd/kintsugi.git`, and it is
**public**. So no real deployment detail belongs in a tracked file — not just credentials but the
server's own address. Secrets and TLS material stay in `.env` and `nginx/tls/`, both gitignored;
every agent's `DEFAULT_API_BASE_URL` and `packaging/config.toml` ships the placeholder
`kintsugi.example.com`, because a real fleet sets its address at install time via `config.toml` or
`PATCHING_AGENT_API_BASE_URL` and never by editing that default. Keep the three agents' defaults
identical — each one's comment claims it is in step with the others.


## Working alongside other sessions

**Several Claude Code sessions are usually working in this one checkout at the same time.** They
share a single working tree, a single index and a single `HEAD`, so the uncommitted changes sitting
beside yours are probably somebody else's, and the branch you are on is not necessarily the one you
checked out.

**Commit the paths you wrote, and nothing else.** `git add -A`, `git add .` and `git commit -a`
stage whatever happens to be dirty, which here means another session's half-finished work — so name
your files instead: `git add web/lib/main.dart web/test/presentation/text_selection_test.dart`.
Check `git status` first and expect to see edits you did not make; leave them alone. This is not
hypothetical: one session's `git add -A` swept another's in-progress `main.dart` and its test into a
commit about the Windows agent's identity handling, so a commit message describing one change
shipped a tree containing two.

**Naming a path is not enough when the other session is editing the same file.** Staging a file
stages all of it, so `git add CLAUDE.md` on a file two sessions are appending to commits both
appends — which is how this very section first went in carrying somebody else's release note. When
`git diff --cached` shows more than you wrote, stage the hunks rather than the file: `git add -p`,
or build the blob you meant (`git hash-object -w`, `git update-index --cacheinfo`), which stages
your version without disturbing what is on disk for whoever is still typing into it.

**Check `HEAD` before amending, and never amend a commit you did not write.** `git commit --amend`
rewrites whatever `HEAD` points at, and `HEAD` moves when another session switches branch — which
happens without warning, because the branch is shared state rather than yours. `git branch
--show-current` and `git log -1` immediately before committing cost nothing; recovering somebody
else's rewritten commit out of the reflog costs a good deal more. For the same reason, re-read a
branch's log right before merging it: commits may have landed on it since you last looked, and they
will go out under your merge unless you notice and say so.


## Where the rest of this lives

This file holds what applies everywhere, and what you have to know *before* you know you need it.
Everything else sits in a `CLAUDE.md` beside the code it describes, and loads only when Claude reads
a file in that directory:

| File | Covers |
|---|---|
| `src/CLAUDE.md` | Backend layering, the auth gap's detail, host removal, script approval, platform buckets, Vanta, vulnerability assessment, Auditing, the remote-control relay |
| `web/CLAUDE.md` | The Flutter admin UI: four layers, polling, `KintsugiTable`, enums on the wire, theme, text selection, the remote-control viewer and terminal, the two Vulnerabilities screens |
| `clients/CLAUDE.md` | What the three agents share: the table of differences, the queue, check-in scheduling, patch cycles, remote-control consent and sockets, the OS facts only two of them report |
| `clients/macos-agent/CLAUDE.md` | Identity file modes, the remote-shell launchd job, TCC, and the code-signing identity that makes a grant outlive a release |
| `clients/windows-agent/CLAUDE.md` | Building via mingw, the serial-number chain, the SYSTEM session helper, ConPTY, self-update's service restart |
| `clients/linux-agent/CLAUDE.md` | The state directory, two root entry points, the fourth root unit, X11 vs Wayland selection |
| `clients/linux-agent-wayland/CLAUDE.md` | The portal, PipeWire, and the one binary in this fleet with a libc floor |
| `nginx/CLAUDE.md` | Location precedence, client-certificate verification, the 495/496 remap |

`ARCHITECTURE.md` at the root is the one file that is not scoped to a directory and not loaded
automatically: sequence diagrams for the six flows — enrolment, check-in, patching, client updates,
script creation and approval, vulnerability management — showing who calls whom in what order. It
holds no reasoning of its own and names the `CLAUDE.md` that owns each detail instead, so read it to
find your way to the right file, not to learn why anything is the way it is.

Couplings that have **two ends in different directories** are path-scoped rules in `.claude/rules/`.
They load when Claude reads a file at *either* end, which directory-scoped files cannot do:

| Rule | Fires on |
|---|---|
| `remote-protocol-mirror.md` | each agent's `remote_protocol.rs` and the viewer's `remote_control_mapper.dart` |
| `hand-mirrored-dtos.md` | C# DTOs, the Rust structs and `web/lib/data/models/` |
| `nginx-routes-and-tls.md` | `nginx/` and `src/Kintsugi.WebApi/` |
| `package-manager-names.md` | the catalog and the agents' `system_info.rs` / `upgrade.rs` |
| `agent-release-and-archive.md` | packaging scripts, `self_update.rs`, `Cargo.toml`, CI |
| `remote-control-timeouts.md` | each agent's `remote_control.rs` and the server's relay |
| `script-approval-repo.md` | the approval publisher and reader |

<!-- Keep this file under ~300 lines. Content that only matters in one directory belongs in that
directory's CLAUDE.md; a coupling whose two ends are in different directories belongs in
.claude/rules/ with a paths: glob naming both. Do not use @path imports to pull any of it back in —
imports are expanded at launch, so they would undo the whole arrangement. -->

## Adding a route: the checklist nothing enforces

Both halves of this are invisible from the C# you are editing, and each has shipped broken.

1. **An agent-facing route** must be added to the exact-match regex in `nginx/default.conf` *and*
   carry `[RequireAgentIdentity]`. Miss the regex and the route is un-gated; nothing in the C# will
   tell you.
2. **A browser-driven route** must carry `[RequireAdminSession]` and live under `/api/admin/`.
   Excluding a route from nginx's regex does not make it browser-only, it makes it certless.
3. **Any new server-side route** needs a `location` in `nginx/default.conf` *above* the SPA
   fallback, which is deliberately the last block in the file — otherwise nginx answers it with
   `index.html`: a 200 containing markup, much harder to diagnose than a 404.

**Agent authentication is two layers, and adding a route needs both.** nginx requires a client
certificate signed by the fleet CA on an *exact-match* regex —
`^/api/(host|applications|patching-policy|upgrade-paths|patch-results|os-patch-results|host-removed)$`
— and forwards the verified Subject CN as `X-Agent-Cert-Cn`. `[RequireAgentIdentity]` then compares
that CN against the `serialNumber` the request body claims (via `IAgentScopedRequest`), so a valid
agent cert can't be used to report data for a different host. **A new agent-facing route is
un-gated until `nginx/default.conf` is edited too** — nothing in the C# will tell you.
`/api/host/enroll` is deliberately outside the regex (an unenrolled agent has no cert yet), as are
the browser-driven `/api/upgrade-paths/...` sub-routes.


**Not every `/api` route is an agent route, and the two auth mechanisms leave a gap between them.**
nginx requires a client certificate on an *exact-match* regex, so nothing under
`/api/upgrade-paths/...` ever matches it — deliberately, since those routes are driven by the admin
UI and a browser has no agent certificate. `Program.cs` then exempts the whole of
`/api` from the sign-in gate, on the reasoning that agents authenticate with mutual TLS rather than
cookies. Each decision is right alone; together they leave a browser-driven route with **no
authentication of any kind**. That shipped: `save` (accepts an arbitrary script) and `sign-script`
(has the server sign it) were both callable by anyone who could reach the server, which is the whole
path from arbitrary text to content every agent runs as root. Both now carry
`[RequireAdminSession]`, which mirrors `Program.cs`'s own gate semantics rather than inventing a
second shape that could drift from it. **Adding a browser-driven route means adding that
attribute** — nothing else will stop it being anonymous, and excluding a route from nginx's regex
does not make it browser-only, it makes it certless.


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

# Admin UI (Flutter web). Analyze, test and build the bundle nginx serves.
cd web && flutter analyze
cd web && flutter test
cd web && flutter test test/presentation/instructions_panel_bloc_test.dart
cd web && flutter build web --release

# Run the whole system (see below — this is the only supported way to run the API *or the UI*)
docker compose up -d --build

```


There is no `IDesignTimeDbContextFactory`, so `dotnet ef` resolves the connection string from
`appsettings.json`, whose value is `Host=db` — only resolvable inside compose. Override
`ConnectionStrings__Database` when running EF tooling from the host.


**Do not expect `dotnet run` to work.** `Program.cs` hardcodes
`PersistKeysToFileSystem("/data/dataprotection-keys")` and the default connection string points at
the `db` service; both only exist inside the container. Run via `docker compose`.
`docker compose build` does not build the tests either — the Dockerfile copies only `src/`. Note
too that the API no longer serves any UI: `dotnet run` would answer `/api`, `/swagger` and
`/health` and 404 everything else, because the admin UI is served by nginx from a bundle
`nginx/Dockerfile` compiles. `cd web && flutter run -d chrome` is the way to work on the UI alone,
and it needs a running `docker compose` for its API calls to go anywhere.

Releasing an agent: bump `version` in that agent's `Cargo.toml` **and regenerate its `Cargo.lock`**,
then merge to `main`. CI (`.github/workflows/ci.yml`) runs every test suite, then builds and tags a
GitHub Release per agent whose version isn't already released — `macos-agent-v0.5.0` and so on, one
`.tar.gz` asset each. It never POSTs to a server; the server pulls, via the Clients screen's
"Refresh clients" (see web/CLAUDE.md). The release body is written by
`.github/scripts/agent-release-notes.sh`: one line per commit since that platform's previous
release tag that touched the agent's own tree
(plus `clients/linux-agent-wayland` for Linux), which is what the Clients screen shows under each
row as "release notes for every newer build" — so a commit subject on an agent change is read by the
administrator deciding whether to roll it out, not only by the next developer.


## Conventions

Comments here explain *why* a decision was made and name the file at the other end of a coupling
(C# doc comments referencing `checkin_schedule.rs`, Rust comments referencing
`EnrollAgentCommandHandler`, each agent's comments naming the other where they diverge). Match that
density and that habit — the cross-references are how this codebase stays navigable. Domain entities
use private setters with static factory methods and behaviour methods; keep invariants in `Domain`,
not in handlers.

**Bump `<Version>` in `src/Kintsugi.WebApi/Kintsugi.WebApi.csproj` in every commit that touches
`src/` or `web/`**, sized to what the commit did: major for a breaking change to a contract an agent
or the admin UI depends on, minor for a new feature, route or screen, patch for a fix or a
refinement. It is the only version a human can see without shelling into a container — the admin
UI's sidebar reads it through `AdminServerController` — so it is what anyone asking "is this
deployment current?" looks at first.

It used to be bumped "on a release", which meant nothing bumped it: thirteen server commits shipped
under 1.2.1, two new screens among them, and the sidebar could not tell a current deployment from a
three-day-old one. That cost real time — a change that was already live on production was hunted
through Docker images because the number had not moved. Note the two things it still does *not*
track, deliberately: each agent's own `Cargo.toml` version (released independently by CI, and the
Clients screen shows those separately), and any change confined to `clients/`, `nginx/` or
`.github/`. And read the current value immediately before editing it rather than assuming — several
sessions share this checkout and all of them now touch this one line. If two bumps collide, take the
higher.
