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
`docker compose build` does not build the tests either — the Dockerfile copies only `src/`. Note
too that the API no longer serves any UI: `dotnet run` would answer `/api`, `/swagger` and
`/health` and 404 everything else, because the admin UI is served by nginx from a bundle
`nginx/Dockerfile` compiles. `cd web && flutter run -d chrome` is the way to work on the UI alone,
and it needs a running `docker compose` for its API calls to go anywhere.

Releasing an agent: bump `version` in that agent's `Cargo.toml` and merge to `main`. CI
(`.github/workflows/ci.yml`) runs every test suite, then builds and tags a GitHub Release per agent
whose version isn't already released — `macos-agent-v0.5.0` and so on, one `.tar.gz` asset each.
It never POSTs to a server; the server pulls, via the Clients screen's "Refresh clients" (below).

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

**A rejected agent certificate never reaches the 403.** `ssl_verify_client optional` means "verify
it if one is offered", not "tolerate a bad one" — a presented certificate that fails to verify
raises nginx's 495 during request processing, *before* any `location` is matched, so the agent
block's `$ssl_client_verify != SUCCESS` test only ever sees `NONE`. Unremapped, 495 goes out as a
bare 400 and the agent reports only "request rejected (HTTP 400 Bad Request)". `default.conf` now
remaps 495/496 to distinct messages, because the two causes need completely different fixes: no
certificate means an unenrolled agent or a TLS-terminating proxy in front eating it, while a
rejected one almost always means the fleet CA was regenerated under an already-enrolled agent.
Do not "fix" a rejected certificate by switching to `optional_no_ca` — verification against the
fleet CA is the entire security property.

**Agent authentication is two layers, and adding a route needs both.** nginx requires a client
certificate signed by the fleet CA on an *exact-match* regex —
`^/api/(host|applications|patching-policy|upgrade-paths|patch-results|os-patch-results|host-removed)$`
— and forwards the verified Subject CN as `X-Agent-Cert-Cn`. `[RequireAgentIdentity]` then compares
that CN against the `serialNumber` the request body claims (via `IAgentScopedRequest`), so a valid
agent cert can't be used to report data for a different host. **A new agent-facing route is
un-gated until `nginx/default.conf` is edited too** — nothing in the C# will tell you.
`/api/host/enroll` is deliberately outside the regex (an unenrolled agent has no cert yet), as are
the browser-driven `/api/upgrade-paths/...` sub-routes.

**Only the macOS per-user process holds the agent identity, and that constrains its file mode.**
It reads the same `identity/` directory the root daemon writes, so the directory is `root:admin
0770` and the key `0640` — `admin` because that is the logged-in administrator's group, and the
per-user process is not root (Homebrew refusing to run as root is the whole reason macOS differs;
the Windows and Linux per-user halves hold no identity and go through their queue instead).
`install.sh` sets that ownership, and `identity.rs`'s `enroll` now sets it again on every
enrollment, because macOS gives a new file its *directory's* group rather than the creating
process's. Without that second call, deleting `identity/` to recover from a regenerated CA — the
documented remedy — recreates it under root's own `wheel`, and the per-user process can never read
its own key again. It fails half-visibly: the root daemon is fine, the host keeps registering, and
only the per-user half stops, presenting no certificate at all and drawing a 403.

**Two separate key hierarchies, kept apart on purpose.** `CaService` mints agent identities;
`ArtifactSigningService` signs script/command *content*. An AI-generated or hand-pasted script
starts **unsigned** — a human must sign it via `POST /api/upgrade-paths/sign-script`, and the agent
verifies against the signing pubkey it pinned at enrollment before executing anything. Do not make
generation sign automatically.

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

Every anonymous route is now closed. `AiSettingsController`, `DeploymentsController` and
`PatchesController` carry the attribute **on the class**, because nothing on them is an agent route
and the recurring failure is a route added later inheriting no gate; `HostsController` and
`UpgradePathsController` are mixed, so theirs is per-action. Two routes were removed rather than
gated, because neither could be secured as it stood:

- `POST /api/upgrade-paths/report-version` set `LatestVersion` fleet-wide for any (application,
  platform), which drives `updateAvailable`, which drives the agent's `is_patchable` — so anyone
  could suppress patching across the fleet by posting the installed version as the latest one. It
  could not take `[RequireAgentIdentity]`, because its body carried no serial number for the filter
  to compare `X-Agent-Cert-Cn` against, and no agent called it. It is redundant besides: the
  update-check coordinator already re-runs each script's own `--update-version` on the server. If it
  returns it needs a `serialNumber`, the attribute, and an entry in nginx's regex.
- `PUT /api/patching-policy` was *not* anonymous — it sits inside nginx's exact-match regex, so a
  client certificate was required. That was the problem: it carried no `[RequireAgentIdentity]` and
  no admin gate, so **any enrolled agent could rewrite the fleet-wide patching policy**, while a
  browser could not reach it at all. Nothing legitimate called it — at the time the Settings page
  dispatched `UpdatePatchingPolicySettingsCommand` through `ISender`, and all three agents only ever
  `GET` this path (`policy.rs`). The `GET` stays; it is what agents poll. The write now lives at
  `PUT /api/admin/settings/patching-policy`, outside that regex and carrying
  `[RequireAdminSession]`, which is exactly where `PatchingPolicyController`'s own note said such a
  route belongs.

Still anonymous by design, and correctly so: `POST /api/host/enroll` (an unenrolled agent has no
certificate; the enrollment token is what protects it) and everything under `/api/agent-packages`
(a self-updating agent has to see what is published before proving anything, and the download is
protected by a signed checksum instead). `/swagger` is also exempt from the sign-in gate, so the
route listing is readable anonymously — disclosure only, but worth knowing.

**Removing a host is two-phase, and the soft-deleted row still owns its name.** The Hosts screen's
delete is a *request*: `RequestHostRemoval` sets `DeletedAtUtc` (so the host vanishes from the list
at once) and `RemovalRequested` (so the next check-in response tells the agent to uninstall itself).
The row is hard-deleted only when the agent confirms via `POST /api/host-removed`. An agent that
cannot authenticate never confirms — and cannot even *learn* it should uninstall, since both routes
are inside nginx's client-certificate regex — so the row lingers forever, invisible but still
holding `Hostname` and `SerialNumber` in unique indexes.

That is not hypothetical: it deadlocked a Windows host whose identity write was failing. Re-register
under a *different* serial (a re-imaged machine, or one whose serial moved between rungs of
`choose_serial_number`) and `CreateHostCommandHandler` — which looks up by serial number only —
inserts, collides on `IX_hosts_Hostname`, and returns a bare 500 whose only clue is a constraint
name in the server log, on a route agents call unattended every hour. So `ReclaimHostnameAsync` now
hard-deletes a **removed** row whose name is being claimed (installed applications go with it;
`installed_applications` cascades on `HostId`), and a name held by a **live** host raises
`ConflictException` → 409 rather than deleting either record on an agent's say-so. Keep that split.
The reclaim is deliberately reachable only when no row matched the reported serial, which is what
leaves the ordinary removal flow intact: a host coming back under its *own* serial still matches
above, still carries `RemovalRequested`, and is still told to uninstall rather than resurrected.

**Script approval is shared through a GitHub repository, and the default branch is the trust root.**
Signing a script is effective locally at once — the human at the console reviewed it — and *also*
opens a pull request against `SCRIPT_APPROVAL_GITHUB_REPO` carrying the script, its metadata and the
signature (`GitHubScriptApprovalPublisher`). The pull request is a **record and a distribution
channel, not a gate**: it is raised after `SaveChangesAsync`, and every failure mode is reported
rather than thrown, because a GitHub outage must not stop a reviewed script from patching the fleet
it was reviewed for. The layout is content-addressed —
`approved-scripts/<sha256>/{<name>.sh|<name>.ps1, metadata.json, signatures/<fingerprint>.json}` —
because a package-manager script is byte-identical for every application that manager handles, so
one review covers all of them (the same reason `FindExistingSignatureForScriptAsync` matches on
content), and because one signature *file per signer* means two servers approving the same bytes
never touch the same path and so never conflict. `.gitattributes` exempts `approved-scripts/**` from
`text=auto eol=lf`: normalizing a PowerShell script's CRLF would change its hash and invalidate
every signature over it.

**An entry is published as what it is, not as the row somebody happened to sign.** The row a human
presses "Sign Script" on is one application's; a package-manager script is every application's. So
`ApprovedScriptIdentity` decides what the metadata, the commit message, the pull request title and
the filename say: a package-manager entry is `homebrew.sh` / `homebrew-self-update.sh` /
`winget.ps1` and is labelled for the manager (never *as* the manager — `Homebrew` would match the
manager's own self-update row in the adoption offer), with `ApplicationIdentifier` dropped because
whichever application the reviewer was looking at says nothing about a script all of them share; an
AI-researched entry keeps the application's own name and is filed under its identifier
(`com.nextcloud.desktopclient.sh`, `Mozilla.Firefox.ps1`). Which of a manager's two scripts an entry
holds is decided by comparing bytes against `BuildScript(true|false)`, not by trusting the row.
The filename is **not** load-bearing: `ApprovedScriptCorpus.ScriptPathsIn` finds the script by
extension and confirms it by hash, which is what keeps entries written under the original fixed
`script.sh` readable, and why an existing `script.sh` is written to again rather than renamed. One
consequence to hold onto: a generic package-manager entry matches no local row's name, so those
entries are **bless-only** — correctly, since this server generates those exact bytes itself and
`ImportApprovedScriptsFromSourceCommandHandler`'s content-match bless already covers them. Adoption
is for AI-researched scripts, where matching on name is exactly right.

**A remote signature is never served to an agent — the importing server re-signs.** Each agent pins
exactly one signing key at enrollment: its own server's. So the Upgrade Scripts screen's "Refresh
scripts" verifies the upstream signature and then signs the same bytes with the **local** key. That
is why this feature needed no change to any of the three agents. Two halves, split on whether
content arrives: *blessing* a local script whose bytes are already approved upstream is automatic
and safe to be (nothing new arrives — it is `SignUpgradePathScriptCommandHandler`'s sibling-row
propagation extended across servers), while *adopting* content this server does not have is a
per-row button a human presses, with the signer's fingerprint beside it.

**Be precise about what verifying an approval proves.** The signer's public key travels in the same
repository as the script it vouches for, so anyone able to write there can edit a script, mint a
fresh keypair, and produce an entry that verifies perfectly. Verification establishes that an entry
is internally consistent and names its signer — *not* that the signer was authorized. Authorization
is the repository's branch protection on the default branch, and nothing else. The one genuinely
verified case is a fingerprint equal to `GetPublicKeyFingerprint()`: a signature this server made,
against a key that never left its private volume. Do not write comments or UI copy that upgrade this
to "verified"; the screen says so plainly and should keep doing so. The consequence worth holding onto:
**a merge to that repository is enough to offer new executable content to every server that
refreshes**, which is why adoption is not automatic, why adoption refuses a row that already carries
a signature (agents may be running it), and why `ScriptLanguages.For` must agree on both sides — a
genuinely-signed `#!/bin/bash` script reaching a PowerShell host is exactly the failure the shared
`generic` bucket used to permit.

**The admin UI is a separate client, and everything it needs is a REST route.** It used to be
Razor Pages that injected `ISender` and dispatched MediatR handlers directly, so most screens had no
API at all. It is now a Flutter web application in `web/`, compiled by `nginx/Dockerfile` and served
as static files by nginx — see "The admin UI" below.

## The admin UI

`web/` is a Flutter web application. `nginx/Dockerfile` compiles it and bakes the bundle into the
nginx image, which is why that image is built rather than pulled: `docker compose up -d --build`
has to stay the one documented way to run the system, and a bundle built on somebody's laptop and
mounted in would make that untrue on a clean checkout.

**Four layers, and the dependency arrow points inwards.** `domain/` holds entities, narrow
repository interfaces and use cases, and knows nothing about JSON or HTTP; `data/` implements those
interfaces and owns every mapping; `presentation/` holds the BLoCs and screens and depends on use
cases; `core/` holds the transport, theme, router and `core/di/injection.dart`, which is the only
file in the app that names a concrete implementation. The repository interfaces are deliberately one
screen's worth each rather than one per layer — a BLoC that reads hosts cannot see the route that
signs a script.

**Entities extend `Equatable` for a reason that is not tidiness.** The screens poll, so value
equality is what makes a poll that finds nothing new emit an identical state and rebuild nothing.

**`GET /api/session` is the bootstrap, and the only anonymous route added for the UI.** It reports
`authenticationSettingsSaved`, `authenticationEnabled` and `signedIn`, which is exactly the state
`Program.cs`'s middleware used to act on by redirecting. It cannot be gated: it is the route that
tells a caller whether to sign in, so gating it would leave a fresh deploy unable to reach the screen
that configures a provider. Everything else the UI calls carries `[RequireAdminSession]`.

**Sign-in stays server-side, and that is a decision rather than an omission.** The client's sign-in
button is a whole-page navigation to `GET /api/auth/challenge`; the provider comes back to
`/signin-oidc`, still handled by the OpenIdConnect handler, which sets the cookie
`[RequireAdminSession]` reads. A browser-side code flow would make this a public client, and
`AuthenticationSettings` requires a client secret precisely because it is a confidential one —
Google's web-application clients require it at the token endpoint regardless, so a browser exchange
would have broken a provider the settings screen offers.

**An expired cookie has to be handled centrally, and getting this wrong is a regression the
migration nearly shipped.** When the UI was Razor Pages, an expired session was answered by an
unconditional 302 that the operator could not miss. A client that only reads JSON gets a 401 — and
if each screen renders that as an error string, an expired session looks like "Not signed in."
printed above a stale table, with the sign-out button hidden because the session the client is
holding still says signed-in. So `ApiClient` raises `UnauthorizedNotifier` on any 401 and
`SessionBloc` re-reads `GET /api/session` when it does, which routes to the sign-in screen through
the same gate a page load would have used. `/api/session` itself is excluded from that
announcement, or a 401 there would loop; a 401 from it is handled where it lands instead, as a
session needing sign-in rather than as a broken server — `UnauthorizedApiException` is an
`ApiException`, so the general clause would otherwise pin the client to the "cannot reach Kintsugi"
screen whose only action re-reads that same route.

**Browser-driven routes live under `/api/admin/`, and the prefix is load-bearing.** `/api/applications`
and `/api/patching-policy` are *inside* nginx's exact-match agent regex, so a browser-driven route on
either path demands a fleet client certificate the browser has not got — and the failure is a 403
with nothing in the C# to explain it. The prefix cannot collide with that regex however it grows.

**nginx's location precedence is the one thing in `default.conf` not to get creative with.** nginx
remembers the longest matching *prefix* and then evaluates regex locations — unless that prefix
carries `^~`, which tells it to stop. So `^~ /api` would become the longest match for `/api/host`,
the agent block's regex would never be consulted, and every agent-only route would be served with no
client certificate at all. The block is a plain `location /api` for that reason. The SPA fallback
(`try_files $uri $uri/ /index.html`) is the last location in the file, so a new server-side route
means adding a location above it or the client answers it with `index.html` — a 200 containing
markup, much harder to diagnose than a 404.

**The UI polls; it does not push.** The three background coordinators already expose their progress
as `*-status` routes designed to be polled, so there is no push channel to consume and adding one
would be new protocol for a UI that only reads. `core/bloc/polling.dart` is the shared mixin. What
changed relative to the pages this replaced is what happens with the answer: a poll emits a state
and the affected widgets rebuild, rather than calling `window.location.reload()`.

**Enums cross the wire as names or as ordinals depending on the type, and that must not be
"fixed".** `UpgradePathStatus`, `UpgradeMethod` and `ScriptApprovalPublishOutcome` carry converters
and write their names; `HostStatus`, `AiProvider`, `AuthProvider`, `PatchingTimeUnit` and
`AgentPackageImportOutcome` have none, so System.Text.Json writes their ordinals. Turning on a
global string-enum converter would break the fleet: all three agents read some of these as ordinals
— `clients/*/src/policy.rs` parses `interval_unit` as a `u8`. `web/lib/core/network/json_reader.dart`
reads whichever form arrives; declaration order in `web/lib/domain/entities/enums.dart` is therefore
load-bearing. `UpgradeMethod` is written back as a *name*, because `LenientEnumConverter` reads
nothing else.

**Two things about the image build that each cost a build to learn.** The Flutter stage is pinned to
`linux/amd64` because Flutter publishes no arm64 Linux SDK, so on Apple Silicon it runs under
emulation and takes minutes. And `.dockerignore` excludes `web/.dart_tool`: its
`package_config.json` records *absolute* paths to the SDK and pub cache of whichever machine ran
`flutter pub get`, so copying it in overwrites the container's own and `dart2js` fails reading
`/Users/<someone>/.pub-cache/...`.

**The theme key is coupled to `web/web/index.html` by hand.** `ThemeCubit` stores the choice through
`shared_preferences`, which namespaces its keys under `flutter.`, so the inline script that paints
the background before Flutter boots looks for `flutter.kintsugi-theme`. Renaming it in one place
needs renaming in the other; nothing checks that they agree.

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

**Two AI providers reach Claude, and the difference is which meter they spend.** `Anthropic` calls
`api.anthropic.com` with an API key and bills metered credits. `ClaudeAgentSdk` runs the `claude`
binary the runtime image installs (Anthropic's apt repository, `stable` channel — package-manager
installs never auto-update themselves, so the version answering research runs changes only when the
image is rebuilt) as a `-p --output-format json` subprocess, authenticating with the one-year OAuth
token `claude setup-token` prints, which bills that subscription's included usage instead. Model
output is indistinguishable between the two; only the bill differs, which is why
`ClaudeAgentSdkClient` **removes** `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_PROFILE`
and the three `CLAUDE_CODE_USE_*` variables from the child's environment rather than merely setting
the token: Claude Code's credential precedence ranks every one of them *above*
`CLAUDE_CODE_OAUTH_TOKEN`, so a `.env` that also carries an API key for the `Anthropic` provider —
an entirely ordinary thing for it to carry — would silently move the whole fleet's research back
onto the API. For the same reason `--bare` must never be added to that command line however
attractive its faster startup looks: bare mode does not read `CLAUDE_CODE_OAUTH_TOKEN` at all and
requires an API key. The empty working directory the client runs in exists because `--bare` is
unavailable — without it the CLI reads `.claude/`, `.mcp.json` and `CLAUDE.md` from wherever it
starts. It runs `--permission-mode dontAsk --allowedTools WebSearch,WebFetch` because `-p` starts in
Manual mode on every plan, which would deny the research tools and answer from memory with nothing
to say it had; and deliberately **not** `--dangerously-skip-permissions`, because this container
holds the fleet CA and the signing key.

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
the direction is reversed. The Clients screen checks the repository's releases on every load and
"Refresh clients" downloads what's newer, rewrites `api_base_url` to this server's own address, and
republishes it locally (`ImportAgentPackagesFromSourceCommandHandler`). That address comes from
`AGENT_API_BASE_URL`, falling back to the address the request arrived on when it is unset.

**The fallback is a guess, and the admin UI's address is frequently the wrong answer.** nginx is
what verifies the agent's client certificate, so anything terminating TLS in front of it — a
gateway, a load balancer, a CDN — ends the mutual-TLS handshake at itself and cannot pass the
certificate on. `AGENT_API_BASE_URL` must name **nginx's own address and `WEB_TLS_PORT`**. Getting
it wrong fails in the quietest way the system has: `/api/host/enroll` is deliberately outside
nginx's client-certificate regex, so the agent enrolls, looks installed, and then 403s on every
authenticated route forever. That is not hypothetical — it shipped, from an earlier version that
derived the address unconditionally and argued it was safe because the plain-HTTP listener only
301s to the TLS one. That argument covers the scheme and the port and misses the front door. The
resolution now happens server-side in `AdminClientsController.ResolveAgentApiBaseUrl` — never from
a value the client supplies, which would be a client-supplied instruction about what to bake into
signed packages — and the screen says out loud when it is falling back.

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
rotates far more often than a build does. Refresh used to be a **Razor Page handler rather than an
API route**, deliberately: `location ^~ /api/agent-packages` is a prefix match with no client
certificate required and `Program.cs` exempts all of `/api` from the sign-in gate, so an API route
would be triggerable by anyone who could reach the server. With the UI a client rather than a
server-rendered page there is no page handler to use, so it is now
`POST /api/admin/clients/refresh` and what carries that reasoning is `[RequireAdminSession]` on
`AdminClientsController` — nothing else does.

**GitHub configuration is database-backed, and nothing may capture it.** The four values that used
to be environment variables (`GITHUB_API_TOKEN`, `AGENT_PACKAGE_GITHUB_REPO`,
`SCRIPT_APPROVAL_GITHUB_REPO`, `SCRIPT_APPROVAL_GITHUB_TOKEN`) now live in `github_settings` and are
edited at Settings > GitHub. The environment is read **exactly once**, by
`SeedGitHubSettingsFromEnvironmentAsync` at startup, and only on a server with no row yet — that
carries an existing deployment across without re-entering anything, and the `.env` entries can then
be deleted. A row existing, even one saved with everything blank, means the environment is never
consulted again; it is a seed, not a fallback, so clearing a value on the page can never be quietly
undone by a stale variable.

The consequence is the part worth remembering: **a value can now change while the process is
running.** Every GitHub client used to read `IConfiguration` in its constructor and pin the token
onto `HttpClient.DefaultRequestHeaders` there, which would ignore every later edit until a restart.
They all read `IGitHubSettingsProvider` per call instead, and attach the token to the individual
request — a typed `HttpClient` instance outlives one call, so a header pinned to it carries whichever
token was current the first time. For the same reason the client interfaces no longer expose
`SourceDescription` / `RepositoryDescription` / `IsEnabled`: those were synchronous properties over
configuration, which is precisely what cannot be captured. Callers that need to display them read the
provider, which is also where the `hobleyd/kintsugi` default is resolved — at read time, so the
default lives in one place rather than being written into every row.

**The settings subnav is alphabetical by label.** AI Agent, Authentication, GitHub, Patching Policy,
Vanta. It is a lookup rather than a workflow, so there is no other order a reader could predict; keep
it that way when adding one.

**Fresh deploys lock everything to the Authentication screen, and nothing redirects any more.**
With no `AuthenticationSettings` row saved, the client pins itself to `/settings/authentication`.
That used to be a 302 from `Program.cs`; the UI is static files in nginx now, so its page load never
reaches this application to be redirected. `GET /api/session` reports the state and the client's
router gates on it — see "The admin UI" above and the long comment in `Program.cs` where the
middleware used to be. The OIDC provider is still configured at runtime from the database
(`DynamicOpenIdConnectOptionsConfigurator`), not at startup.

## Compliance evidence: the Vanta integration

Kintsugi pushes its view of the fleet into Vanta as a private "Build integrations" data source
(https://developer.vanta.com/reference/build-integrations.json). Configured at Settings > Vanta,
run on a timer by `VantaSyncBackgroundService`, and **off until an administrator turns it on** —
`VantaSettings` is deliberately *not* seeded from the environment the way `GitHubSettings` is, since
that seeding exists only to carry deployments off variables that used to be there, and these never
were.

**Two of the spec's thirteen resource types are synced, and the eleven omissions include two that
look like the obvious fit.** A host becomes a `VulnerableComponent`; each out-of-date application on
it, and each pending OS update, becomes a `PackageVulnerabilityConnectors` record naming that
component. What is *not* synced is `macos_user_computer` and `windows_user_computer`, and the reason
is not effort: every one of `drives`, `users`, `systemScreenlockPolicies`, `isManaged` and
`autoUpdatesEnabled` is **required** by those schemas, and Kintsugi collects none of them. An empty
`drives` array is not a gap in a compliance tool, it is an assertion about disk encryption — so
filling those from defaults would put invented evidence behind real controls. (There is no
`linux_user_computer` endpoint at all, so a third of the fleet could not be covered even if the data
existed.) The Vanta screen says all of this out loud; keep it saying it.

**`severity` is a number the administrator picks, and the CVSS fields are absent rather than
nullable.** Vanta makes severity mandatory on a 0-10 scale. Kintsugi compares an installed version
against a latest known version; it has no CVE feed, no CVSS vector and no reachability analysis. So
`VantaSettings.Severity` is one configured constant applied uniformly, `VantaPackageVulnerability`
has no `CveId`/`Cvss3Score`/`Cvss3Vector`/`IsReachable` properties **at all** (a test asserts that),
and each record's own description says it came from a version comparison rather than a feed. Do not
"improve" this by deriving a score from staleness — a plausible number in a compliance record is
worse than an honest constant.

**Every sync is a state-of-the-world replacement, which makes an empty payload a deletion.** Vanta
deletes any `uniqueId` previously sent and now omitted, so there is no chunked or incremental form of
this: `VantaResourceBuilder.Build` produces the complete set in memory and only then does
`SyncVantaResourcesCommandHandler` send it. That handler carries the one guard that matters — **zero
components is never sent**, because a query returning no hosts would otherwise wipe the whole
inventory, and a fleet with no hosts has nothing to sync anyway. The asymmetry is deliberate and must
not be "fixed": an empty *package* list **is** sent, and is how a fleet that has just finished
patching clears what Vanta still holds for it.

**Order matters, and a failed component sync cancels the package sync.** Each package names its
component by `uniqueId`, so components land first; if that call fails, packages are not sent at all
rather than sent as orphans.

**`uniqueId`s are derived, never row identity.** A host keys on its serial number — the value that
*is* this system's host identity (it is the certificate CN) and the only one that survives both a
`Reregister` hostname change and a delete-and-re-enroll, which mints a fresh `Host.Id`. An
application keys on (serial, application name) and explicitly **not** on `InstalledApplication.Id`,
because `RegisterApplicationsCommandHandler` deletes and recreates every row on each routine
inventory report: a row-keyed id would change on every check-in, and since each sync replaces
everything, Vanta would see the fleet's entire vulnerability history deleted and recreated daily.

**`collectedTimestamp` is `Host.LastSeenUtc`, not now**, and a host that has never checked in is
dropped from the sync entirely rather than stamped with the current time — nothing has been collected
from it. Its applications go with it, since a package naming an absent component is an orphan.

**One access token, and that shapes the concurrency.** Vanta issues one active token per application
and *revokes the previous one the moment a new one is requested*, so `VantaAccessTokenProvider` is a
singleton holding a single cached token behind a `SemaphoreSlim`, keyed on the credentials it was
obtained with (rotating the secret on the settings page therefore invalidates it implicitly).
`VantaSyncCoordinator` allows one run at a time for the same reason, and "Sync now" answers `409`
rather than queueing. The token is attached to each individual request, never to
`HttpClient.DefaultRequestHeaders` — the same rule the GitHub clients follow, and for the same
reason: a typed client outlives one call.

**`VantaSettings.ConsoleBaseUrl` is its own setting and is not `AGENT_API_BASE_URL`.** It is the
address every synced record links back to, so it must be the *browser's* door, not nginx's agent one
— see "The fallback is a guess" above. It cannot be derived from the request either, because the
sync normally runs on a timer with nothing in flight. HTTPS is enforced at save time in the domain
entity, because Vanta requires it and the alternative is an opaque rejection a day later.

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

**A signed script is never rewritten by a deployment, and editing one of those bodies changes
nothing until a human says so.** `RegisterApplicationsCommandHandler` used to rewrite `Script` from
the builder on every routine inventory report, under the belief that "the script content for a given
(manager, isSelfUpdate) case never changes". It changes whenever one of those bodies is edited — so
what that actually meant was that a background report could swap the content of a signed row,
content the fleet's agents may be executing right now, on the strength of a deployment nobody was
watching. It is exactly what `UpgradePath.AdoptApprovedScript` refuses to do, and a report has less
business doing it than a human pressing Adopt. Now a row that carries a `ScriptSignature` keeps its
script exactly as reviewed and only `LatestVersion` moves; an *unsigned* row is still written from
the builder, which is how a fixed script reaches rows nobody has approved yet.

Two things follow. `UpgradePath.Apply` drops `ScriptSignature` whenever the content it is replacing
actually differs (same for `Command`/`CommandSignature`) — the invariant that a signature never
outlives its bytes, which now only ever fires on a deliberate act (a force-refresh, a pasted script,
`TakeServerWrittenScript`) rather than in the background. And because nothing takes the newer script
by itself, the Upgrade Scripts screen has to say one exists: `PackageManagerCatalog.CurrentScriptFor`
gives the query handler the script this build would write, `LocalScriptDto.NewerServerScriptAvailable`
flags a row that differs, and `TakeServerWrittenScriptCommand` replaces one — **unsigned**, so the
new text reaches no host until someone has read it, and one "Sign Script" then covers every row
holding those bytes via `FindExistingSignatureForScriptAsync`. Do not make that automatic on the
grounds that the server trusts its own generated content: the review is the only thing standing
between an edited builder body and root execution on every host.

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
so patching lives in the root service and the per-user process holds no identity and makes **no
network call at all** — it decides *when*, and asks. macOS is the odd one out precisely because
Homebrew *refuses* to run as root and installs into a user-writable prefix. The queue directory is
`root:root 1733` (a drop-box: anyone may write, only root may read or list), which is the Linux
spelling of the macOS queue's `root:admin 0770` and needs no group — "local administrators" is
`sudo` on Debian, `wheel` on Red Hat, and neither elsewhere.

**"No network call at all" includes the patching policy, and 0.5.0 got that wrong.**
`/api/patching-policy` sits inside nginx's client-certificate regex, so there is no such thing as
fetching it without an identity — but the Linux per-user process tried, under a comment asserting
the route was ungated. Every Linux host with a graphical session therefore 403'd once a minute
forever while the root service, having deferred to that process, patched nothing; the host went on
reporting healthy check-ins the whole time, and the symptom only appears on day two because a
freshly enrolled host isn't due until then. The fix is the Windows arrangement, which had it right:
the root service fetches the policy on **every** check-in — before registration, and regardless of
whether it then defers — and writes `/var/lib/kintsugi-agent/policy.json` `0644`; the per-user
process only ever `policy::load_cached`es it. Keep `fetch` private to the root side on both
platforms, and don't reintroduce a per-user fetch of anything.

**The state directory is `0711`, and `0700` silently kills the drop-box.** A queue at `1733` is
unreachable if nothing outside root can *traverse* its parent, so no user can write a request or a
heartbeat — and because the per-user process cannot list the directory either, `is_dir` on the
queue fails exactly as it would if the agent were not installed, which is what 0.5.0's warning
wrongly claimed. `0711` is traverse-only: root is still the only one who can list the directory or
read `identity/` (still `0700`, and deliberately). `install.sh` sets it, and
`config::repair_directory_modes` re-asserts both modes on every root check-in — required, not
belt-and-braces, because `self_update` replaces the binary and never re-runs the installer, so
hosts already in the field have no other repair path.

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

**Windows and Linux serial numbers are frequently placeholders.** `HKLM\HARDWARE\DESCRIPTION\System\BIOS`
and `/sys/class/dmi/id/product_serial` read the same SMBIOS field and inherit the same junk from board
vendors: "To Be Filled By O.E.M.", "Default string", "0", "Not Specified" (which is what every guest
of a bare `qemu-system-x86_64` reports). The serial *is* this host's identity — it becomes the
certificate CN, which `[RequireAgentIdentity]` compares against every request body — so two hosts
sharing one would share a host record, a certificate, and each other's data.
`system_info::serial_number` in both agents therefore screens against a placeholder list and
**refuses to enroll** rather than inventing a value. macOS has no equivalent failure mode.

**One blank field is not one missing serial, and the Windows agent walks a chain.** The registry key
above is the value an administrator sees on the sticker, but it is routinely *absent* on hardware
whose `Win32_BIOS.SerialNumber` carries that same serial — so reading only the registry reported no
serial on a machine that plainly has one. `choose_serial_number` therefore tries, in order: the
registry serial, `Win32_BIOS.SerialNumber`, `Win32_ComputerSystemProduct.IdentifyingNumber`,
`Win32_BaseBoard.SerialNumber` (SMBIOS type 2, reachable *only* via CIM — the registry key exposes
the baseboard's manufacturer, product and version and no serial at all),
`Win32_SystemEnclosure.SerialNumber`, `Win32_ComputerSystemProduct.UUID`, and only then the
`MachineGuid`. All five CIM fields come from one PowerShell pass (`FIRMWARE_IDENTITY_SCRIPT`), read
lazily — a host whose registry value is populated never spawns it. `SMBIOSAssetTag` sits beside the
chassis serial and is deliberately *not* read: an asset tag is administrator-assigned and frequently
identical across a purchase batch.

The order is by how well each field identifies *this physical machine*, and the two rungs at the
bottom are where the reasoning is easy to get backwards. `MachineGuid` is **last**, not second: it
identifies a Windows *installation*, sysprep regenerates it, and an image deployed *without* sysprep
gives every clone the same one — which are exactly the machines whose SMBIOS serial is a placeholder
too. The SMBIOS system UUID outranks it because it is per-machine and set per-VM by every hypervisor,
but it needs its own screening (`PLACEHOLDER_SYSTEM_UUIDS`): the all-`F` form means "field omitted",
and `03000200-0400-0500-0006-000700080009` is a constant shipped by some VMware and Dell firmware, so
accepting it would enroll a whole fleet as one host.

**Widening that chain re-identifies hosts, and `identity::load` will not notice.** It reads the
certificate off disk and compares nothing, so a host already enrolled under a fallback identity —
its `MachineGuid`, or a placeholder that used to pass screening — starts sending the newly-found
serial in request bodies while presenting a certificate whose CN is the old value.
`[RequireAgentIdentity]` then 403s every authenticated route, permanently, while the host still looks
enrolled. The remedy is the documented one (delete `identity/` and let it re-enroll), but nothing
prompts for it and `self_update` delivers the change unattended. So before shipping any change to
what `serial_number` returns: check the Hosts screen for GUID-shaped or placeholder-shaped serials,
because those are precisely the hosts that will need re-enrolling.

**The Windows identity directory is SYSTEM and Administrators only, and a service running as
anything else cannot read its own identity.** `identity::restrict_identity_permissions` strips
inheritance and grants exactly `S-1-5-18` and `S-1-5-32-544`, once, inside `enroll` — there is no
repair pass re-asserting it, unlike Linux's `config::repair_directory_modes`. The failure mode is
quiet in a specific way: `icacls` reads as perfectly correct, because it *is* correct for the two
SIDs it names. Check `sc.exe qc KintsugiAgent` before believing an ACL. `install.ps1` pins
`obj= LocalSystem` on both branches now, but nothing stops a hardening baseline changing it later.

`identity::load` used to answer that with `None` — the same answer it gives a host that has never
enrolled — so the agent reported "this agent has not enrolled an identity yet", re-enrolled every
check-in forever, spent a certificate issuance on the server each time, and died on the *write*,
naming `agent.crt` (the first file written) rather than whichever read actually failed. It now
separates `NotFound` from every other error and refuses to start an enrollment it knows will be
refused. The remedy is to delete the identity directory **outright** rather than its contents:
`create_dir_all` then recreates it inheriting from the parent, which is what clears stale per-file
permissions.

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
  checking in at once. Two consequences: the file must hold a publicly-trusted chain, and if a proxy
  in front used to own renewal, it no longer does — whoever renews has to copy the new pair to this
  host and reload nginx, or the fleet goes dark on expiry day.
- **That chain must be complete, and `curl` will not tell you whether it is.** rustls does no AIA
  chasing: if `fullchain.pem` omits an intermediate, rustls cannot fetch the missing link and fails
  with `invalid peer certificate: UnknownIssuer`, while curl and browsers succeed because their
  bundles are newer or they go and fetch it. This has already bitten once — a `fullchain.pem`
  truncated to leaf + `Let's Encrypt YR1` terminated at `ISRG Root YR`, which is not in the macOS
  system trust store; the cross-signed `Root YR` (issued by `ISRG Root X1`, which *is*) was the
  third cert and had been dropped. Verify with the store the agent actually uses, not with curl:

  ```bash
  # count what the server sends — a truncated chain is the common failure
  echo Q | openssl s_client -connect <host>:443 -servername <host> -showcerts 2>/dev/null \
      | grep -c 'BEGIN CERTIFICATE'
  # and confirm the agent itself is happy, which is the only test that counts
  grep 'UnknownIssuer' <that platform's agent log>
  ```
- CI's release tags (`<platform>-agent-v<version>`) are parsed by `GitHubAgentPackageSourceClient`
  to work out which platform and version a release is. Renaming a tag on either side silently stops
  that platform ever being found again — a refresh just reports nothing new.
- The agent-package platform namespace (`"macos"`, `"windows"`, `"linux"`) is *not*
  `PlatformBucket`'s namespace (`"macOS"`, `"Windows"`, `"Linux"`, `"pm:..."`). They name different
  things; don't unify them.
- The script-approval token is deliberately *not* the read-only API token. The latter exists only to
  lift GitHub's anonymous rate limit and is handed to the AI research client and the agent-package
  source client as well, so reusing it would silently give both of them `contents:write` and
  `pull_requests:write` on the approval repository. Unset means signing approves locally and raises
  no pull request — the Upgrade Scripts screen says so, because the absence of an audit trail is
  otherwise only discoverable by looking for pull requests that were never opened.
- `ApprovedScriptCorpus` is the *only* description of the approval repository's layout, and both ends
  of the round trip go through it — the publisher writing an entry and the reader parsing one. A path
  or field changed on one side only means an approval that publishes fine and imports as nothing.
- GitHub's `/tarball/{ref}` nests everything under a `{owner}-{repo}-{shortsha}/` directory.
  `ReadArchiveFiles` strips that first segment; without it nothing matches `approved-scripts/` and
  the result is indistinguishable from an empty corpus.
- `PackageManagerCatalog`'s names are the strings agents report in `InstalledApp.package_manager`.
  A rename on either side silently stops an entire manager's applications resolving.
- A package-manager row is only patchable if the *agent* reported an `applicationIdentifier` for that
  installed application — `is_patchable` requires one for any `Script` row, and it comes from the
  `InstalledApplication`, not from the `UpgradePath` (which always has one, falling back to the
  name). The Windows and Linux agents set it for every managed package; the macOS agent leaves it
  unset for Homebrew formulae/casks, which is why those rows do not currently patch.
- **nginx loads the fleet CA's public certificate at startup and exits without it, so the API has
  to create that file before the first agent exists.** `Program.cs` calls
  `EnsureAgentFleetCaExists` for exactly this reason. `CaService` generates the CA lazily, on the
  first `GetCaCertificatePem`/`IssueClientCertificatePem` — which is to say from
  `EnrollAgentCommandHandler`, on the first enrollment — and an enrollment has to arrive through
  nginx. Without that startup call a clean deployment deadlocks: `docker compose up` reports the
  api service healthy and nginx in a restart loop, complaining about a missing certificate nothing
  was ever going to write. Do not make the CA lazy again on the grounds that nothing needs it
  until an agent turns up.
- Volumes that must survive a redeploy: `dataprotection-keys` (or every session is signed out),
  `agent-ca-private` / `agent-ca-public` (or the whole fleet must re-enroll), `agent-packages`,
  `db-data`.
- Rust request/response structs mirror C# command/DTO shapes by hand with explicit `serde(rename)`
  — changing a command's JSON shape means changing the matching struct in **all three** agents.
- Windows PowerShell 5.1 decodes a BOM-less `.ps1` using the system ANSI code page, not UTF-8. The
  Windows agent writes every script with a UTF-8 BOM for exactly that reason, and the
  server-written ones are kept ASCII-only as well.
- **`brew info --json=v2` writes a cask stanza as a bare string when it names one item and as an
  array when it names several**, and reading only the array form is a security bug rather than a
  missed optimization. `strings_in` in the macOS agent's `system_info.rs` handles both. It shipped
  wrong: `nextcloud` declares `uninstall delete: "/Applications/Nextcloud.app"` as a single string,
  so the bundle name never reached `cask_app_bundle_names`, `scan_applications_folder` stopped
  recognizing it as cask-installed, and it was reported a *second* time as a standalone application
  — carrying a `CFBundleIdentifier`. That identifier is exactly what `is_patchable` requires before
  it will run a `Script` row, so a Homebrew row the per-user process **cannot** patch became
  eligible for patching. Every cycle then quit Nextcloud (Homebrew's `start_upgrade` quits the app
  before installing, and only reopens it on success), failed, and left the client stopped.
- **A `pkg`-artifact cask cannot be upgraded by the macOS agent at all, and the failure is
  disguised.** `Cask::Pkg#uninstall` pipes the NUL-joined BOM (371 KB for nextcloud) into
  `sudo -u root -E -- /usr/bin/xargs -0 -- /bin/rm --`; the per-user process has no TTY and no
  `SUDO_ASKPASS`, so sudo exits before reading and Ruby's `Errno::EPIPE` surfaces as
  `Error: <cask>: Broken pipe`. Homebrew's `SystemCommand#each_output_line` writes stdin *before*
  starting its output-reader thread, so sudo's real stderr is never reported. There is no
  arrangement that fixes this inside Homebrew: `brew` refuses to run as root (`brew.sh`'s
  `check-run-command-as-root`), `as-console-user` immediately drops back to the console user,
  `SUDO_ASKPASS` still needs a real password, and `HOMEBREW_SUDO_THROUGH_SUDO_USER` is only
  passwordless if brew is already root. Root-requiring casks are therefore **not agent-patchable**;
  do not try to route them through the root queue by having the daemon drive `brew`.
- A new server-side route needs a `location` in `nginx/default.conf` *above* the SPA fallback, or
  nginx answers it with `index.html` — a 200 containing markup rather than a 404, which is
  considerably harder to diagnose. The fallback is deliberately the last block in the file.
- Rust structs are not the only hand-mirrored copies of a C# shape any more: `web/lib/data/models/`
  maps every DTO the admin UI reads, and `web/lib/domain/entities/enums.dart` mirrors the enums in
  declaration order because several of them cross the wire as ordinals. Changing a DTO's JSON shape
  means changing the matching mapper as well as the three agents — and unlike the agents, nothing
  in CI cross-checks the two, because the client is compiled separately.
- The Vanta sync mirrors Vanta's own JSON shapes by hand in `VantaResources.cs`, the same way the
  Rust structs and `web/lib/data/models/` mirror this system's. Nothing validates them against
  `build-integrations.json`; a required field added upstream shows up as a rejected sync with
  Vanta's message in the settings screen's status line, which is the only place it will appear.
- `VantaResourceBuilder`'s package `externalUrl` builds the Applications screen's own deep link
  (`/applications?status=update-available&host=…`), so it is coupled to `UpgradePathStatusKey` and
  to `app_router.dart` reading those query parameters. Change either and every synced record links
  to an unfiltered page — a 200 that looks fine, which is why nothing would report it.
- `web/pubspec.yaml`'s `environment: sdk:` constraint and `FLUTTER_VERSION` in `nginx/Dockerfile`
  have to stay compatible. Bumping one without the other fails at image build time rather than at
  merge, which is the good failure but only if somebody builds the image.

## Conventions

Comments here explain *why* a decision was made and name the file at the other end of a coupling
(C# doc comments referencing `checkin_schedule.rs`, Rust comments referencing
`EnrollAgentCommandHandler`, each agent's comments naming the other where they diverge). Match that
density and that habit — the cross-references are how this codebase stays navigable. Domain entities
use private setters with static factory methods and behaviour methods; keep invariants in `Domain`,
not in handlers.
