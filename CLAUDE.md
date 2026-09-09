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
# Once, ever: mints the fleet code-signing identity straight into this repository's Actions secrets,
# which is what keeps a host's Screen Recording and Accessibility grants across releases. Nothing
# local holds the key. See "One certificate signs every build" under Remote control.
clients/macos-agent/packaging/create-signing-identity.sh --set-secrets
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

# The Linux agent's Wayland backend, which is its own crate because it links libpipewire (see
# "Couplings"). Needs libpipewire-0.3-dev >= 0.3.65 — debian:12 or newer, not ubuntu:22.04.
cd clients/linux-agent-wayland && cargo test
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
"Refresh clients" (below). The release body is written by `.github/scripts/agent-release-notes.sh`:
one line per commit since that platform's previous release tag that touched the agent's own tree
(plus `clients/linux-agent-wayland` for Linux), which is what the Clients screen shows under each
row as "release notes for every newer build" — so a commit subject on an agent change is read by the
administrator deciding whether to roll it out, not only by the next developer.

**The lock file is half the bump, and forgetting it fails after the merge rather than before it.**
A crate's own version is recorded in its `Cargo.lock` as well as its `Cargo.toml`, and every cargo
invocation in CI passes `--locked` (`ci.yml:122`, `190-191`, `228`, `275`) — which exists precisely
to refuse a lock file that doesn't match. So a bump that touches only `Cargo.toml` dies at
`error: cannot update the lock file ... because --locked was passed`, *before compiling anything*.
The failure reads as "the agent tests failed" when the tests never ran, and because it stops at the
test step the release job never fires, so the tag is silently never cut. A bare `cargo test` will
not reproduce it — the flag is the whole difference — so verify a bump with the same invocation CI
uses:

```bash
cd clients/<platform>-agent && cargo test --locked
```

Running any ordinary `cargo build`/`test`/`check` after editing the version updates the lock file in
passing; the trap is doing that in a scratch copy of the tree and committing only from the original.

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

One thing that container will *not* do on an Apple Silicon Mac: build the
`x86_64-unknown-linux-musl` release CI ships. `rust:1-slim` is arm64 there, so its `musl-gcc` cannot
cross-compile `ring`'s C, and the failure looks alarming — a `cc-rs` error deep in a dependency
rather than anything about the target. Use `aarch64-unknown-linux-musl` to check the static-linking
property (it holds or fails for the same reasons), or `--platform linux/amd64` if the exact artifact
matters. And note `cargo build` for the *host* works on macOS too, which is the quickest syntax check
of all — see the coupling note on keeping it that way.

It links no C library and no GUI toolkit (see the `ksni` note in its `Cargo.toml`), so the stock
image needs nothing added. Every output-parsing function — `flatpak list`, `snap list`, the DMI
serial screening, `apt-get --just-print upgrade` and the four other package managers' listings —
takes a `&str` and has tests against captured real output, for exactly the reason the Windows
`winget list` parser does.

**Verifying the Wayland backend actually captures.** `cargo test` checks the framing, the pod and
the slot semantics; none of that says whether PipeWire will hand over a frame, and every way of
getting that wrong fails as a stream that connects and delivers nothing. The negotiation needs no
compositor — any PipeWire producer exercises the same code — so the cheap decisive test is:

```bash
# in a container with pipewire, wireplumber, gstreamer1.0-pipewire and libpipewire-0.3-dev
pipewire & wireplumber & sleep 2
# mode=provide is load-bearing: in its default mode pipewiresink looks for somewhere to render and
# publishes nothing, so there is no node to target.
gst-launch-1.0 -q videotestsrc pattern=smpte is-live=true \
    ! video/x-raw,format=BGRx,width=640,height=480,framerate=30/1 ! pipewiresink mode=provide &
sleep 3
NODE=$(pw-dump | ... Stream/Output/Video ...)
cargo run --example capture-node -- "$NODE" > frames.bin   # then decode the framing
```

A real compositor is only needed to exercise the *portal*, which is a separate question and worth
standing up once: `debian:trixie-slim` with `sway pipewire wireplumber xdg-desktop-portal
xdg-desktop-portal-wlr`, `WLR_BACKENDS=headless WLR_RENDERER=pixman`, `XDG_CURRENT_DESKTOP=sway`
(without which the frontend matches no backend and every request fails with no detail), and
`~/.config/xdg-desktop-portal-wlr/config` naming an `output_name` before the portal starts — it
otherwise looks for slurp or wofi to ask a human. That environment confirms the ScreenCast/no-
RemoteDesktop split wlroots really has, which is the view-only path. It does *not* deliver frames:
sway's headless output has no DRM device, so the GLES2 renderer will not initialise and pixman's
screencopy never offers the portal a format. `grim` working there while the portal does not is how to
tell that apart from a bug in this code.

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
one had shipped in the branch of the Homebrew script that answers for Homebrew itself and in the
prompt text recommending the pattern to the AI; nothing surfaced it, because the failure is a null
`LatestVersion`, which is indistinguishable from "no update available".

**Every package manager has one script, and the manager's own row is told apart at runtime.** Each
manager used to get two texts from `BuildScript(isSelfUpdate)` — `homebrew-self-update.sh` beside
`homebrew.sh`, `winget-self-update.ps1` beside `winget.ps1` — because the manager's own row needs
different handling from the applications it manages. That second text cost more than it bought: the
Applications screen nests every managed application under the manager's row and shows the manager's
script there once on behalf of all of them, which is only honest if the manager's bytes *are* its
children's bytes. So `RecognizedPackageManager.BuildScript` takes no argument, every builder returns
one text, and where the manager's row differs the *script* branches on `--appName` being the
manager's name — the name each agent reports the manager under (`system_info::HOMEBREW_NAME`,
`WINGET_NAME`, `FLATPAK_NAME`) and the rule `PrepareUpgradePathScanQueryHandler` recognizes the row by.
Homebrew is not a formula (GitHub's releases redirect for the version, `brew update` alone for the
upgrade); winget is not a winget package under its own name (the winget-cli releases redirect, and
`Microsoft.AppInstaller` is what gets upgraded); Flatpak is a distribution package (declines to answer
a version, upgrades through the distribution's own manager). Snap and Chocolatey need no branch at
all — snapd is a snap and `chocolatey` is a Chocolatey package, both reported under exactly that id.
Three things follow. `brew update` is Homebrew's own self-update as well as the index refresh, so on
macOS **every** application upgrade upgrades Homebrew too and the manager's row needs nothing more —
do not put a blanket `brew upgrade` back on it, which would patch every formula on the host regardless
of which rows a human has approved; the other managers get no such side effect, deliberately, since
upgrading App Installer from inside a running `winget` is not something to do on every package. One
signature now covers a manager and everything it manages, and `ApprovedScriptIdentity` publishes one
entry per manager, never a `-self-update` one. And a server upgraded across this change shows the old
self-update bytes on the Upgrade Scripts screen as a row with a newer server-written script, to be
taken and signed like any other — `PackageManagerDisplayName(…, isSelfUpdate: true)` survives only to
label those legacy rows.

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
the filename say: a package-manager entry is `homebrew.sh` / `winget.ps1` / `flatpak.sh` — one per
manager, the manager's own row included — and is labelled for the manager (never *as* the manager —
`Homebrew` would match the manager's own row in the adoption offer), with `ApplicationIdentifier`
dropped because whichever application the reviewer was looking at says nothing about a script all of
them share; an AI-researched entry keeps the application's own name and is filed under its identifier
(`com.nextcloud.desktopclient.sh`, `Mozilla.Firefox.ps1`). Whether an entry is the manager's script
is decided by comparing bytes against `BuildScript()`, not by trusting the row.
The filename is **not** load-bearing: `ApprovedScriptCorpus.ScriptPathsIn` finds the script by
extension and confirms it by hash, which is what keeps entries written under the original fixed
`script.sh` readable. One consequence to hold onto: a generic package-manager entry matches no local
row's name, so those entries are **bless-only** — correctly, since this server generates those exact
bytes itself and `ImportApprovedScriptsFromSourceCommandHandler`'s content-match bless already covers
them. Adoption is for AI-researched scripts, where matching on name is exactly right.

**A pull request is raised for new content only, and "already approved" is decided by the entry, not
the signature.** `GitHubScriptApprovalPublisher` first asks whether
`approved-scripts/<sha256>/metadata.json` exists on the default branch, and if it does, signing
reports `AlreadyApproved` and writes nothing — whichever server's signature the entry carries. Once
the bytes are on the trust root they are approved for every server reading it; a second server
signing them locally is doing what a bless does (re-signing approved content with its own key so its
own agents can verify it), and a bless raises no pull request. The check used to compare this
signer's signature *document* against the one on the branch, and that never matched: an ECDSA
signature is randomised per signing and the document carries `SignedAtUtc`, so every re-sign of an
already-merged script — the Homebrew script taken from a newer build and signed on a second server,
say — opened a pull request rewriting one signature file. Do not put a document comparison back; the
`signatures/<fingerprint>.json`-per-signer layout still exists so two servers approving the same
*new* bytes at the same time never conflict, not so that every server publishes its attestation.

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

**Every list is one `KintsugiTable`, and its header row is where the layout goes wrong.** Three
things there are load-bearing and each of them shipped broken. A header label is routinely wider
than the column beneath it — "Hosts Installed On" sits over a count badge, "Actions" over one icon
— and a `Row` that cannot fit its child does not shrink it, it paints it over the next column, so
the label is `Flexible` and wraps to a second line. `Table` centres a cell vertically by default,
and only some header cells carry a filter control, so the labels of the ones that do not floated to
the middle of the height those set; the header cells are `TableCell(verticalAlignment: top)`
individually, **not** the table's `defaultVerticalAlignment`, because the body rows and the
expanded instructions panel do want the middle default. And a `Text` cannot make a word narrower,
so `_labelFloor` sizes every column to at least the longest *word* in its label — applied in the
widget rather than left as a minimum each screen remembers per column, which is exactly the
coupling nothing would have enforced.

Two consequences worth holding onto. `minWidth` is a floor, not a width: the table takes the
panel's full width when there is more of it, which means that width has to be measured **outside**
the horizontal `SingleChildScrollView` — one gives its child an unbounded width by definition, so
a `LayoutBuilder` inside reads `maxWidth` as infinity every time and the table lays out at exactly
`minWidth` on every display. And set `minWidth` from what the *cells* need, because the panel's
horizontal scrollbar is the only thing between a reader and the last column, and on web that
scrollbar is not drawn until something scrolls. `test/presentation/table_header_layout_test.dart`
pins all of it at a width narrow enough to force every case; none of them throws in a release
build, and none is visible in a table whose labels happen to be short.

**Enums cross the wire as names or as ordinals depending on the type, and that must not be
"fixed".** `UpgradePathStatus`, `UpgradeMethod`, `ScriptApprovalPublishOutcome`,
`RemoteControlConsent` and `RemoteControlSessionKind` carry converters and write their names; `HostStatus`, `AiProvider`, `AuthProvider`, `AuditProvider`,
`PatchingTimeUnit` and `AgentPackageImportOutcome` have none, so System.Text.Json writes their
ordinals. Turning on a global string-enum converter would break the fleet: all three agents read
some of these as ordinals — `clients/*/src/policy.rs` parses `interval_unit` as a `u8`.
`web/lib/core/network/json_reader.dart` reads whichever form arrives; declaration order in
`web/lib/domain/entities/enums.dart` is therefore load-bearing. `UpgradeMethod` is written back as
a *name*, because `LenientEnumConverter` reads nothing else.

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

**Text is selectable app-wide, and the line that does it needs an Overlay it has to bring itself.**
Flutter web paints its text into a canvas, so there is no DOM for the browser's own selection to act
on and *nothing* is selectable unless a `SelectionArea` says so. `main.dart` puts one in
`MaterialApp.builder`, which is inside the `Theme` and `Localizations` the selection toolbar needs
and above the Navigator — so it covers the routes `AppShell` does not (sign-in, the
cannot-reach-Kintsugi screen) and both dialogs, which are routes pushed on that same Navigator. It
is wrapped in `Overlay.wrap` because `SelectableRegion` asserts an `Overlay` ancestor (it floats its
toolbar and magnifier in one) and `builder` runs above the Navigator that would otherwise supply
one; without it the first frame throws "No Overlay widget found" — **in debug only, since it is an
assert**, so a clean `flutter build web --release` says nothing about it and only running the app
does. `test/presentation/text_selection_test.dart` pumps that same arrangement for exactly that
reason.

Its focus node is supplied rather than defaulted, for `skipTraversal`, and that argument is not
tidiness either: on web — and *only* on web — `SelectableRegion` wraps its child in a `Stack`
holding an `HtmlElementView` for the browser's own right-click menu, the browser reports a
view-focus change before that Stack's first layout, and the traversal sort reads every node's
`rect`, which asserts `hasSize` on a render object that has none. Skipping traversal keeps the node
out of the sort. **No test in `web/test/` can catch that one**: `kIsWeb` is false under `flutter
test`, so the VM never builds that `Stack`. `cd web && flutter run -d chrome` and reading the
console is the only detector, which makes it worth doing after any change to this wiring.

Two things follow. **Do not use `SelectableText` in `lib/`**: inside a `SelectionArea` it is a
selection *island* that a drag starting outside it stops at, so the two that predated this made
their own text the part a drag across the screen excluded — including the script dialog's script,
which is the one thing anyone opens that dialog to copy. And both themes state
`textSelectionTheme.selectionColor`, because Flutter's fallback is a flat 50% grey which on the dark
palette's near-black background is a smear rather than a highlight — a selection that copies
correctly and looks broken.

**Three independent background coordinators** — upgrade-path scan, per-application refresh, and
update-check — each registered twice in `Program.cs`: the concrete type for the hosted service
(which needs writer-side methods) and a narrow interface for Application handlers. Follow that
shape when adding another. Update-check re-runs each resolved script's own `--update-version` mode
and makes no AI call.

**Every upgrade script is one of two languages, decided by its platform bucket.** `ScriptLanguages.For`
maps a bucket to bash (macOS, Linux, Homebrew, App Store, Flatpak, Snap) or PowerShell (Windows,
winget, Chocolatey), and that one function governs three things that must never
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

**A CrowdStrike-managed Windows estate installs from a rendered script instead, and the pinned
checksum is the whole security design.** An estate that pushes software through CrowdStrike does not
have someone downloading a tarball from the Clients screen on each host, so the Windows row's
Download cell carries a second button, "Deploy Script" — a silent PowerShell installer
(`WindowsBootstrapScript`, `GET /api/admin/clients/windows/bootstrap-script`) that downloads the
release archive **from GitHub** and refuses to install it unless its SHA-256 matches a literal
written into the script. Nothing here terminates that GitHub connection, so TLS alone vouches for
nothing — and the realistic threat in an estate running CrowdStrike at all is its own
TLS-inspecting proxy, which holds a certificate the host's trust store accepts. What makes the pin
worth something is that the script arrives through CrowdStrike, a separately authenticated channel,
so the literal is independent of the connection it is checking.

Three consequences follow, and each is a way to break this without noticing.

- **The pin must never be something an administrator types.** A hand-transcribed hash fails closed
  on a wrong paste, and the obvious fix for that failure — re-deriving it from whatever downloaded
  — is not a control at all. So the script is rendered by the server with the hash, `api_base_url`
  and the current enrollment token already in it. That is also why the route is on
  `AdminClientsController` and inherits its `[RequireAdminSession]`: the rendered script carries a
  live credential.
- **`AgentPackage.UpstreamSha256` is not `AgentPackage.Sha256`, and using the latter would fail
  every install.** `Sha256` is over the archive stored here, *after* `api_base_url` was rewritten
  into it; the script downloads the pristine bytes GitHub serves. The import records the upstream
  hash and the URL it came from before the rewriter touches the stream, and
  `RecordUpstreamProvenance` backfills a Windows row that predates the column on the next "Refresh
  clients" — Windows only, since spending a download per platform on a pin nothing else reads would
  be waste on a button people press often. A row that carries no pin renders a reason, never a
  script with an empty one.
- **The button belongs on the row, not in the section header.** It went in beside "Refresh
  clients" first and was missed outright — a reader looking for something to do with the Windows
  build looks at the Windows build's row, and a page-level action bar is where global actions live.
  Both buttons in that cell stay labelled rather than collapsing to an icon, since being found is
  the whole problem the placement fixes.
- **The script installs; it does not upgrade, and it does not reimplement `install.ps1`.** It
  verifies, extracts into a directory stripped to SYSTEM and Administrators (SYSTEM's `$env:TEMP` is
  `C:\Windows\Temp`, which any user may write to — verifying and then extracting somewhere writable
  leaves a window in which the checked bytes and the executed ones are different files), points the
  packaged `config.toml` at this server, and hands off to the archive's own `install.ps1`. A second
  copy of that installer would drift on three things that each fail quietly: `obj= LocalSystem`, the
  queue ACL granted by SID rather than by the localized name "Users", and the BOM-less
  `config.toml` write. A re-run against an installed host exits 0 — the agent self-updates from this
  server, so reinstalling an older pinned build over a newer running one is a step backwards.

Authenticode is the answer this replaces and is unavailable: nothing signs the Windows binary today.
The verification is written so a signature check can be added beside the hash rather than instead of
it. And be precise about what the pin proves — it is only as good as this server's own fetch from
GitHub at import time, which went over ordinary TLS. That is one fetch at a controlled point rather
than one per endpoint behind whatever proxy each site runs; it is not an attestation.

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

**The settings subnav is alphabetical by label.** AI Agent, Auditing, Authentication, GitHub,
Patching Policy, Vanta. It is a lookup rather than a workflow, so there is no other order a reader
could predict; keep it that way when adding one.

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

## Audit event shipping: the Auditing settings

Settings > Auditing names the logging platform a record of what happens here is shipped to —
Datadog, Grafana Loki, Google Cloud Logging, AWS CloudWatch Logs, Azure Monitor, Splunk HEC, or any
endpoint that takes JSON over HTTP. It is modelled on the Authentication screen: one provider chosen
from a list, the fields that provider needs, and setup instructions for exactly that provider beside
them.

**Nothing ships events yet, and that is the current state rather than an oversight.** `AuditSettings`
is written and read and nothing consumes it; the configuration was built first so the credential and
the destination exist before there is anything to send. What the screen's instructions promise is
therefore a specification for whatever implements the sending — the URL paths, header names and
label values named there (`/api/v2/logs` with `DD-API-KEY`, `/loki/api/v1/push` under a single
`app="kintsugi"` label, `Authorization: Splunk <token>` with sourcetype `_json`, …) are what an
operator granting a credential from those steps is entitled to have arrive. Change one and change
the instructions with it.

**Changing the provider drops the stored secret, unlike every other settings screen.** A blank secret
on the way in means "keep the stored one" — the page never received the real value, so it cannot send
it back unchanged — but that only holds for the *same* provider. A Datadog API key is not an AWS
secret access key, and carrying one across would ship a credential issued by one vendor to another on
the first event. `AuditSettings.Apply` is where that happens, and the screen says so beside the field.

**Three places state which fields a provider needs, and they must agree.** `AuditSettings.Apply`
keeps the invariant true whoever writes to it; `UpdateAuditSettingsCommandValidator` duplicates it
deliberately, so a bad save lands under the field that caused it rather than as one sentence at the
top; and `auditing_screen.dart` decides which boxes to show and restates the requirements as
instructions. The validator's messages are keyed by C# property name, which is how a field error
finds its box — so `Region` is the Datadog site *and* the AWS region, and `ClientId` is the AWS access
key ID, the Azure application ID and the Grafana Cloud username. Those columns are shared on purpose;
renaming one to suit a single provider breaks the other two.

**`AuditProvider` crosses the wire as an ordinal**, like `AuthProvider` and for the same reason, so
declaration order in `web/lib/domain/entities/enums.dart` mirrors the C# enum and new members are
appended, never inserted. The secret is never returned by any route — `AuditSettingsDto` carries
`HasSecret` instead, which is what lets the form honestly offer "leave blank to keep the existing
one".


## Remote control

An administrator can take control of a host's screen, keyboard and mouse from the Hosts screen's
Connect action, or open a **terminal** on it from the Terminal action beside it. Both are the same
session underneath — one request, one relay, one audit row — and `RemoteControlSessionKind` on the
wire is the whole of the difference.

**A shell session asks nobody, and that is the deliberate exception to everything below.** It is
the access an administrator of this fleet already has by other means — it is what they would reach
SSH or a remote PowerShell session for — and the only reason it is routed through this agent is
that SSH would need an inbound port on every managed machine, which is exactly what the relay
design exists to avoid. So it reports `RemoteControlConsent.NotRequired` in place of a decision,
and the compensating control is that the same `remote_control_sessions` row a screen request writes
is written for it, naming who asked and when. Three things follow and none of them is optional.
`NotRequired` is accepted **only** for a shell, checked in `RemoteControlSession.RecordConsent` and
again in `RemoteControlRelaySession.LatchConsent` — the second is not belt-and-braces, it is the
latch the socket gate actually reads, so enforcing it in the entity alone would let an agent open
*capture, keyboard and pointer* on a host shown no dialog simply by claiming no permission was
needed. The shell always runs as the account that already holds this host's fleet identity —
**root** on macOS and Linux, **SYSTEM** on Windows, and **never the logged-in user on any
platform** — which is what makes "nobody is asked" defensible: it is not a session inside somebody
else's desktop, it is the administrative access an SSH key would already give. It is stated on the
wire (`ShellInfo.user`) and shown in the viewer rather than left to be remembered, so a host that
ever answered with a user account would be visible as the different thing it is.

**A terminal needs no desktop, and decoupling that from reachability is what makes the feature
useful.** Both the Linux and Windows agents used to hold their control socket *only* while somebody
was logged in, so "reachable" meant "somebody is sitting at a desktop". That is right for a screen
session and quite wrong for a shell: most of a Linux fleet is servers with no graphical session at
all, and those are precisely the hosts an administrator wants a terminal on. Both now hold the
socket whenever the host has an identity, treat the desktop as a *capability* rather than a
precondition, and answer a screen request that arrives without one with
`ConsentOutcome::Unavailable` — the agent saying "there is nobody here" at once, rather than the
host being invisible and the administrator being told the agent is unreachable. **macOS is still unreachable with nobody logged
in**, for both kinds: its *control* socket is held by the per-user process, so there is nothing to
receive the request in the first place. Only the shell's execution moved to root, not the
negotiation — moving the control socket too would mean porting the Linux split (root holds the
socket, per-user connects back over a local one) to macOS wholesale.

The PTY runs in whichever process already holds the identity and needs no helper: the resident root
unit on Linux (no `remote_ipc` hop — nothing crosses it for a shell) and the **service itself** on
Windows via ConPTY, with no `session_launcher` and no SYSTEM session helper. Everything the Windows
helper exists for is about a desktop, and requiring one would put a logged-in user back in the way of
the host most likely to want a shell.

**macOS needs a handoff for that, and it is a third root job.** Remote control lives in its per-user
process, because the screen belongs to a GUI session the root daemon has not got — so the per-user
process answers the request on the control socket it already holds, flushes that answer, and drops a
request naming the session id into `remote-shell/`. launchd's `WatchPaths` starts
`kintsugi-agent --remote-shell`, which opens the **media socket itself** and runs a root PTY on it.
That needs no server change at all, and the reason is worth holding onto: the two sockets of a
session are independent, and the server pairs a media socket by `(serialNumber, sessionId)` and
authenticates it by the certificate nginx verified — which the root daemon presents because it reads
the same `identity/` directory. Nothing server-side can tell, or needs to.

Three things about that follow. It is **its own launchd job**, not a fourth `queue::RequestKind`:
the main queue is drained by the check-in daemon, launchd never runs two instances of one job, and a
session held open for a support call would otherwise stall this host's check-ins, patches and
self-update for its whole length. A **forged request buys nothing** — the server refuses a media
socket for a session it did not create for this serial and has not seen answered, so the only id
that works is one an administrator has already opened a shell for; this is the main queue's "the
worst it can do is start an already-approved upgrade early" in its narrowest form, and it is why the
request carries a session id and nothing else. And **the root check-in installs that plist, not
`self_update`** — `remote_shell::install_job_if_absent`, called from `run_daemon` before anything
that can fail over the network, creating the drop-box `root:admin 0770` and `launchctl
bootstrap`ping the job when either is missing. Putting it in `self_update` is the obvious place and
it does not work, for a reason worth stating once: **a self-update is performed by the binary that
is already installed**, so an update path can only install a job the *previous* release knew about.
0.9.5 shipped exactly that mistake — its own `install_binary` installed the plist, 0.9.4's did not,
and every Mac that reached 0.9.5 by self-updating got a binary understanding `--remote-shell` with
no job to run it under. What that looks like is a terminal that never opens: the per-user process
cannot create the drop-box (its parent is root's), the server reports "the other end never
connected", and only the per-user log names a path. So this is the macOS spelling of Linux's
`config::repair_directory_modes`, and it is required for the same documented reason — `self_update`
never re-runs the installer, so a host in the field has no other repair path. Two consequences. The
job description is compiled into the binary (`remote_shell::LAUNCHD_JOB_PLIST`, an `include_str!` of
the packaged file), because a check-in has no archive to read from; and its *contents* are still
written **only when absent**, so an administrator's edits survive and a change to the packaged plist
reaches no host that already has one.

**Installing when absent was not enough, and what it missed is an ownership defect the whole
self-update shares.** `tar -xzf` run as root restores the uid and gid recorded *in the archive* —
whichever account built the release — and `fs::copy` on APFS is `fclonefileat`, which clones the
owner and the timestamps with the bytes. So every macOS self-update quietly replaced three
root-owned files with user-owned ones, and each failed in its own unrelated-looking way: **launchd
refuses a LaunchDaemon it does not find root-owned** (`Bootstrap failed: 5: Input/output error`,
which says nothing about ownership), so terminal sessions were requested and never served;
`AppStoreUpgradeScript` refuses a `kintsugi-mas` not owned by root, so App Store rows stopped
patching; and `/usr/local/bin/kintsugi-agent` — the binary launchd executes as root — became
writable by a local account, which is a root escalation for whoever owns it. packaging/install.sh
had this right with `install -o root -g wheel` all along; every self-update since undid it.
`extract_and_install` now passes `--no-same-owner` and `install_over` asserts `root:wheel`, but a
flag only helps future updates — so `self_update::repair_installed_ownership` and
`remote_shell::ensure_job_installed` re-assert both on every check-in, and the latter also
bootstraps the job whenever launchd has not got it. That is what makes a host that has *already*
self-updated into the broken state heal itself rather than needing a reinstall.

**Nothing is shown on the Mac while a shell session runs**, deliberately. The menu bar reports a
*screen* session, because somebody's screen is being watched; a root shell is not a session inside
anyone's desktop, the other two agents announce nothing either, and a notice the per-user process
raised it could not reliably clear — the session runs in a different process, on a socket that one
cannot see.

**Neither socket is new, and no nginx change was needed.** A shell session uses the same standing
control socket and the same per-session media socket a screen session does; the media protocol
gained two message kinds and one text message and nothing else. Keep it that way — a
`/api/admin/remote-shell` location would be a second thing to keep in step with the agent regex for
no gain.

The rest of this section is about **screen** sessions, which are the ones with a consent rule.

**Nothing happens until the person at the keyboard says yes.** The whole feature rests on three
properties, and none of them is optional or configurable: consent is asked for every session and
names the requesting administrator; silence is a refusal; and while a session runs it is visible in
the menu bar with a way to end it. A fourth, durable one sits behind them —
`remote_control_sessions` records every *request*, including the refused ones, the timed-out ones
and the ones against a host that never answered, because "an administrator asked to watch this
laptop and was told no" is precisely the event an auditor is looking for.

**Two auth mechanisms, one on each end, and that is why this is a relay rather than a direct
connection.** The agent's sockets arrive on `/api/remote-control`, inside nginx's exact-match
client-certificate regex, carrying `[RequireAgentIdentity]` so the verified CN must equal the serial
number in the query string. The browser's arrives on `/api/admin/remote-control/...`, outside that
regex and carrying `[RequireAdminSession]`. Neither end could be authenticated by the other's
mechanism, and mutual TLS can only be verified by whatever terminates it — which is nginx. A
peer-to-peer or TURN-relayed design re-terminates somewhere holding no fleet CA, so it would need a
second, parallel auth mechanism *and* an inbound port on every managed Mac. Same constraint as "the
fallback is a guess" above.

**The server relays the media protocol without parsing it, and that is load-bearing.** Once the two
sockets are joined, `RemoteControlSessionBroker` copies bytes between them with message type and
boundaries preserved and nothing in between reading either direction. So the JPEG tiling, the
pointer coordinate space and the keycode mapping are a contract between
`clients/macos-agent/src/remote_protocol.rs` and `web/lib/data/models/remote_control_mapper.dart`
**alone** — adding a capability to the viewer needs no server change, and nothing in the server will
ever catch the two ends drifting apart. `web/test/data/remote_control_mapper_test.dart` asserts the
exact bytes the agent's own `encodes_a_tile_header_big_endian` test produces, which is the only
thing that does.

**One route for the agent's two sockets, because nginx's regex matches a single path segment.** The
standing *control* socket (`?serialNumber=`) and a per-session *media* socket
(`?serialNumber=&sessionId=`) share `/api/remote-control` and are told apart by query string. That
also means `[RequireAgentIdentity]` works unchanged — it falls back to an action argument named
`serialNumber`, and a WebSocket handshake has no body for it to read. The route has its own `=`
location in `default.conf` rather than another alternative in the regex, because a WebSocket needs a
read timeout measured in hours and putting that on the agent block would apply it to `/api/host`
too, where a request holding a worker for an hour is the worse failure.

**The control socket is standing, and it is the only push channel in the system.** Everything else
an agent does is a request it makes when it has something to say; remote control is the one case
where the server has to reach a host, and an hourly check-in cannot carry "somebody would like to
see your screen now". So the per-user process holds one socket open for its life, with reconnect
backoff. Sessions get their own socket so a frame stream can never queue behind a control message,
and so a session dropping does not cost the host its reachability.

**Consent timeouts have the opposite polarity to patching, and the code keeps them apart.**
`dialogs::ConfirmChoice::TimedOut` means "nobody was at the desk, so count it as a delay" — the
user never refused and patching happens regardless. `RemoteControlChoice::TimedOut` means **nobody
consented**, and is treated exactly as a refusal. They are separate enums for that reason; reusing
the first would have put the safe default one careless `match` arm away. The dialog's default button
is Deny for the same reason, since AppleScript reports the default button as `button returned:` even
when it dismissed the dialog itself.

**A delay the dialog spent waiting is not a delay to be served again, and getting that wrong made
the countdown twice as long as the policy says.** The confirmation dialog's giveup *is* one delay
period (`patch_cycle` passes `policy.delay_seconds()` as the timeout), so by the time it fires, the
time a delay buys has already been spent. `register_delay` — right for an explicit "Delay" click,
which asks for a fresh period — then postponed by another one on top, so eight one-hour delays
counted down over sixteen hours: dialog for an hour, an hour of nothing, dialog again with the
count decremented. `ScheduleState::register_unanswered_prompt` is the unanswered case instead: it
charges the budget for however many whole delay periods actually elapsed while the dialog stood
there, and leaves the cycle due *now*, so the next poll tick re-asks at once and the count falls
8 → 7 → 6 once per period. Crediting elapsed periods rather than assuming one is what covers sleep
— a machine that slept with the dialog open wakes to a giveup that fires immediately, and spends
the delays that passed rather than starting them again. The budget running out needs no special
case: the next tick finds `can_delay` false and shows the "no delays left" dialog, which is where
the five-minute notice comes from. **That dialog is the warning rather than a preamble to it**: it
states the period, stands there for as much of it as the user leaves it up, and `remaining_warning`
hands `execute` whatever is left, so the notice is five minutes in total — somebody who reads it and
clicks OK still gets the rest of the period, and a host with nobody at it waits five minutes instead
of the ten that showing both in series cost. Nothing here is a running timer — `is_due` compares wall
clock against a persisted absolute epoch, which is why sleep, hibernation and a restart all need no
wake detection.

**The relay is in-memory and single-process.** A session pairs two sockets that must land in the
same process, so a second API replica behind a load balancer would break remote control specifically
unless both were routed to the same instance. Nothing does that today — compose runs one `api` — but
it is the assumption to check first if that changes.

**A host with several monitors is watched one at a time, and the picker is a media-protocol
addition alone.** `DisplayInfo` carries a `displays` list and an `activeDisplayId`; the viewer sends
`{"type":"select-display","id":…}` back; the agent restarts its capture there and resends
`DisplayInfo`. **Nothing in the server changed for any of it**, which is the property "the server
relays the media protocol without parsing it" exists to buy — and equally means nothing server-side
would catch the two ends drifting, so `web/test/data/remote_control_mapper_test.dart` and the three
agents' `remote_protocol.rs` tests assert the same JSON from both sides.

Five things about it are load-bearing, and the first is the one that fails on the commonest desk.

- **The encoder must be told to send a whole frame on every switch, explicitly.** `FrameEncoder`
  finds changes by diffing against the previous frame, and two identical monitors — an ordinary
  office setup — are the *same size*, so its own geometry check sees nothing changed and it happily
  diffs the new display's pixels against the old display's, sending only the tiles that differed and
  leaving fragments of the previous monitor everywhere the two agreed. `force_full_frame` is what
  each agent calls; a test in all three pins it.
- **The viewer keys the change on `activeDisplayId`, for a narrower reason than it first looks.**
  Every size in a `DisplayInfo` is unchanged across a switch between identical monitors, so that
  field is the only one that moves — and `RemoteControlState` is `Equatable` so a poll finding
  nothing new rebuilds nothing, which means an equal state is never emitted. The *tiles* are safe
  without it, because the bloc clears them on every geometry message and so emits a differing state
  whenever there was a picture. What it protects is the **picker**: an announcement arriving with no
  picture on screen — two in a row, which the Linux backend produces by announcing whenever the
  geometry differs from what it last sent — would be dropped, leaving the dropdown naming the
  display the session had just left and the entry for the one it is on inert when clicked.
- **Everything held is released before the pointer space moves.** A modifier or a mouse button down
  across a switch would otherwise stay down with nothing on either screen to explain it — the same
  invariant `release_all` already keeps at the end of a session, at the one other moment the
  coordinate space changes underneath somebody's hand.
- **Ids are opaque and agent-defined, deliberately.** A `CGDirectDisplayID` on macOS, a one-based
  index into Windows' monitor enumeration, a RandR monitor (or zero for the whole root window) on
  X11, a PipeWire node id on Wayland. The viewer echoes one back and interprets nothing, so there is
  no shared numbering to keep in step; the *label* is built by the agent too, because only the host
  knows that one of its screens is "Built-in Display" or "HDMI-1" — a label composed in the browser
  from a resolution says nothing about two identical monitors. An empty list means "offer no
  picker", which is what an agent from before this sends and what a one-display host sends, and the
  viewer shows the control only where there is a genuine choice.
- **A failed switch never ends the session.** A monitor can be unplugged between the list being
  drawn and the choice being made; the agent logs it, stays on the display it was on, and says so in
  the next `DisplayInfo`. Ending the session instead would cost the administrator a granted session
  and the host's user another consent dialog over a pulled cable. For the same reason the viewer's
  picker is stateless — it shows what the agent last *reported*, never what was clicked.

**How the four backends reach a second display differs completely, and two of them are a crop rather
than a different source.** macOS restarts the ScreenCaptureKit stream against another `SCDisplay`
and moves `InputInjector`'s origin, which the injector already had because `CGEventPost` works in
global points. Windows and X11 both already had a device context or a root window spanning the
*whole* virtual desktop, so a switch is a source rectangle inside it — which is why both offer "All
displays" as an entry of its own and macOS does not. Wayland is the one that has to ask somebody
else; see below.

Two platform consequences worth knowing before debugging one. **Windows needed
`MOUSEEVENTF_VIRTUALDESK` and a virtual-desktop divisor together, and either alone puts the pointer
somewhere else** — `MOUSEEVENTF_ABSOLUTE` normalises over the *primary* monitor without the flag, so
coordinates spanning the desktop get squeezed onto one screen. On a single-monitor host the two
rectangles are the same numbers and the whole conversion is the identity, so a wrong one tests
perfectly clean; `input_injection::PointerSpace` is a separate testable type outside the
`#[cfg(windows)]` module for exactly that reason, and its tests use a desktop with a **negative**
origin, which is what a monitor placed left of or above the primary gives. And **the Windows session
helper has to remember which display was picked**, because Windows switching desktops (a UAC prompt,
the lock screen) rebuilds the capture from scratch — a rebuild that passed `None` would quietly drop
the session back onto the primary monitor with nothing to explain it.

**The default display changed on Linux and Windows multi-monitor hosts, and that is deliberate.**
Both now start on the primary monitor rather than the whole desktop: two monitors sent as one
picture are twice as wide for the same 1600-pixel budget, so both arrived at half the resolution of
the one anybody was looking at. "All displays" is one click away, so nothing is lost. macOS never
had the whole-desktop form to lose.

**Wayland's display list is the outputs the host's user agreed to share, not the monitors
attached** — a different thing from the other three, and it cannot be widened from this side. The
helper asks `SelectSources` for `multiple(true)`, the portal's own picker decides what comes back,
and `describe_streams` reports whatever it granted. Three consequences. A two-monitor Wayland host
that shared one output honestly offers one entry, and the viewer shows no picker. **A host that
granted a session before this change keeps returning one output**, because the stored restore token
remembers that single-output grant until somebody revokes the permission in their desktop settings —
that is the portal remembering an answer, not the call being ignored, and it will read exactly like
the compositor limitation the persist-mode bug above produced. And the switch is **asynchronous**:
the helper tears down its PipeWire stream and negotiates another, so frames keep arriving from the
previous display for a moment. `FormatMessage` therefore carries the `node_id`, the agent's
`active_display_id` follows the *frames* rather than the request, and `remote_session` announces the
geometry by comparing it against what it last sent — which is also, incidentally, the first thing
that handles a monitor mode change or a hotplug mid-session on that backend.

**The switch in the Wayland helper is a fresh stream, and the shape it takes is forced.** A PipeWire
stream may only be touched from the thread driving its loop, and that thread is inside
`mainloop.run()` for the whole session — so the request arrives over PipeWire's own channel (whose
callback runs *on* that thread), records the wanted node and quits the loop, and `capture::run` loops
round to connect a new stream. Two things then fall out for free that would otherwise have needed
deliberate work: `KIND_FORMAT` is re-sent before the first frame of the new display, because each
pass has its own writer thread and so its own "have I sent a format yet", and the old stream is fully
torn down before the new one links. `examples/capture-node.rs` is still how to exercise any of this
without a compositor — two `pipewiresink` producers and a switch between their node ids.

**The full-screen frame gives the session a bounded height, and the picture has to be told to fit
it.** `RenderFlex` hands a `Column`'s *non-flexible* children an unbounded main axis, so
`RemoteScreenView`'s `AspectRatio` derives its height from the width alone — which is right inside
`PageScaffold`'s own scroll view and wrong in full screen, where a host screen a different shape from
the window ends up taller than the window with the bottom of somebody's desktop reachable only by
scrolling. So `_FullScreenFrame` uses a plain `Expanded` with **no scroll view** and passes
`fillsViewport`, which wraps the picture in `Flexible`. Both halves are needed and neither works
alone: `Flexible` inside a `Column` of unbounded height throws, and a scroll view would put the
height back to unbounded. A 4:3 host in a wide window is the case that fails — 689 pixels of overflow
— and `test/presentation/remote_control_display_test.dart` pins it, along with the terminal, which
has no intrinsic height at all and so needs the bounded box rather than merely tolerating it.

**The admin UI takes the whole browser window for a session, and the request has to ride the click
that opened it.** `requestFullscreen` needs *transient user activation* — about five seconds after a
gesture in Chrome — so `RemoteControlScreen` asks in `initState`, which runs on the same turn as the
Connect press. Asking after consent arrives would be asking up to sixty seconds after any gesture
and would be refused every single time. A refusal is ordinary rather than exceptional (a bookmarked
URL opened by pressing Enter has no gesture behind it), so it is answered with a "Full Screen" button
rather than an error — pressing it *is* the gesture the browser was waiting for. Three further
things. `FullScreenController` is an interface in `core/platform/` for the same reason
`PageNavigator` is: `package:web` is unavailable under `flutter test`, where `kIsWeb` is false, so a
screen that reached for the DOM could not be pumped at all. `AppShell` drops its 240px sidebar while
full screen and the screen drops `PageScaffold`'s heading and the panel inset, because that width and
height *is* what full screen is for — but the action row stays, since Disconnect is the one control
a session must never be without. And the shell watches a **stream** rather than trusting the
screen's last request, because Escape and F11 leave full screen without anything in this app being
asked.

**`ui.Image` and `CGEventSource` both need releasing by hand.** The viewer keeps decoded tiles as
live `ui.Image`s keyed by position rather than compositing to an offscreen surface, so a repaint is a
few `drawImageRect` calls — but each holds a native texture the garbage collector does not account
for, so every replaced or discarded tile is disposed explicitly. On the agent side
`InputInjector::release_all` runs on **every** path out of a session including a dropped socket: a
session that ends while the remote user happens to be holding Command otherwise leaves the Mac's own
owner with Command stuck down, and nothing on screen explaining it.

**Both TCC permissions fail silently, which is why they are checked before consent is asked.**
`CGEventPost` without Accessibility is dropped with no error and no return code — the session shows
the screen perfectly and ignores the mouse. ScreenCaptureKit without Screen Recording produces
either nothing or a desktop with every window missing. So `describe_restrictions` checks both up
front and the consent dialog lists whatever will not work, rather than leaving it to be discovered
mid-call.

**The signature in `publish-release.sh` is load-bearing, and `cargo build` alone is not enough.**
The linker signs an arm64 slice as "linker-signed" — no designated requirement — and signs an
x86_64 slice not at all, and TCC accepts neither as an identity. What that looks like is not an
error: System Settings shows the binary added and switched on under both Screen Recording and
Accessibility, and the process still fails both checks, however many times the rows are removed and
re-added, restarted or not. 0.6.0 shipped that way; the tell was the system `TCC.db` holding a
`csreq` naming two cdhashes that matched neither slice of the installed file.

**One certificate signs every build, and that is what makes a grant outlive a release.** TCC
records a binary's designated requirement when somebody grants Screen Recording or Accessibility
and re-checks the running process against it on every access. An *ad-hoc* signature's requirement is
`cdhash H"..."` per slice — a hash of that exact build — so it satisfied the grant it was given and
nothing afterwards: `self_update` replaces the binary unattended, so **every release used to orphan
both permissions across the whole fleet**, with System Settings still showing the agent switched on.
So `packaging/create-signing-identity.sh` mints one long-lived self-signed code-signing certificate
(`Kintsugi Agent Signing`) and `release-macos` signs every published build with it — so the
requirement is `identifier "kintsugi-agent" and certificate leaf = H"..."`, which every future
build satisfies. Four things follow.

- **The key lives in the repository's Actions secrets and nowhere else.** The release job is the
  only thing that builds the binary a host installs and self-updates to, so it is the only thing
  that needs to sign; a copy on somebody's laptop would be a second copy of a fleet credential for
  no gain, and a *second identity* there would be worse than none — a host hand-installed from a
  locally signed build and then self-updating from a differently-signed release loses its grants
  anyway, for a reason neither log explains. So the setup script pushes the PKCS#12 straight into
  `MACOS_SIGNING_CERTIFICATE_P12` / `_PASSWORD` and keeps nothing, `release-macos` **fails** when
  those are missing rather than quietly signing ad hoc, and `publish-release.sh` run by hand signs
  ad hoc and says so — what that produces is a package for one server, not the fleet's release, and
  a human is reading the warning. There is no backup of the key: losing it costs the fleet another
  re-grant, which is cheaper than a copy of it existing somewhere.
- **Signing asserts what it produced.** `publish-release.sh` refuses to publish a package whose
  requirement carries a `cdhash` clause, in the same spirit as the `lipo -archs` and
  not-dynamically-linked assertions elsewhere — a signature that quietly came out cdhash-only is a
  fleet-wide grant wipe that no log names. It asserts the *absence* of `cdhash` rather than any
  particular wording, because a self-signed leaf reads `certificate leaf = H"..."` and an
  Apple-anchored one says more, and betting on a spelling would fail a release over a good
  signature. The release job checks its own end too: an imported certificate with no trust behind
  it is *quiet* — `security import` reports "1 identity imported" and `find-identity -v` then finds
  0 valid ones — so it asserts the listing and then signs a throwaway file, because an identity
  `find-identity` lists can still be one `codesign` refuses. `install.sh` says the same thing about
  a packaged binary it is handed, as a diagnostic: there is nothing an installer can do about it.
- **Nothing on a managed Mac needs the certificate.** Validating a signature is not trusting its
  signer, and the requirement only compares the leaf's hash; only the thing that *signs* needs the
  private key and the `add-trusted-cert` line (without which `codesign` refuses the identity with
  `CSSMERR_TP_NOT_TRUSTED`) — which is the release job, for the length of one run, in a keychain it
  creates and throws away. A locally built agent is therefore ad-hoc signed and its grants last
  until the next `cargo build`; `install.sh` says so rather than leaving it to be discovered.
- **Moving to it costs one final re-grant per Mac**, since the requirement changed — both rows
  removed and re-added, and the per-user process relaunched
  (`launchctl kickstart -k gui/$(id -u)/au.com.sharpblue.kintsugiagent-ui`), because a process that
  was denied stays denied until it reconnects to WindowServer. After that a release should cost
  nothing. TCC keys its row on the path as well as the requirement, so it is the installed
  `/usr/local/bin/kintsugi-agent` that keeps its grants; a binary run out of `target/release` is a
  different row and asks again however it was signed.

**MDM pre-approval is now fillable and still unverified.**
`packaging/kintsugi-remote-control.mobileconfig.example` needs a `CodeRequirement` matched against
the binary's signature, and the stable requirement above is exactly what it was waiting on. What is
not established is that a PPPC profile honours a requirement naming a *self-signed* leaf — Apple's
guidance assumes a Developer ID, and nobody has tried this through MDM here, so verify it on one
enrolled Mac rather than deploying it fleet-wide (the file says so at length, including how far back
`kTCCServiceScreenCapture` is grantable at all). Until then both permissions need a human at each
Mac — once, rather than once per release — and Accessibility specifically **cannot be granted from
its prompt at all**: macOS only offers to open System Settings, where someone then has to find the
binary in a list (`/usr` is hidden in the file picker; ⌘⇧G and type `/usr/local/bin`).

**A browser cannot forward every keystroke, and that is permanent.** ⌘W, ⌘Q, ⌘T and ⌘Tab are claimed
by the browser and the OS before any page handler runs, so `RemoteKeyCombinations` offers them as
buttons that send an explicit down/up sequence — Force Quit (⌘⌥⎋) most usefully. Keys are sent as
USB HID usages (`PhysicalKeyboardKey.usbHidUsage`) rather than characters, because a virtual keycode
names a *position* and the host applies its own layout: send the character and an administrator on a
US keyboard controlling a French host types the wrong letters.

**A host is reachable only if somebody is logged in**, which is a stronger statement than the Hosts
screen's own status. "Online" there means a check-in within the last interval, up to an hour ago;
reachable here means a per-user agent process holds a socket right now. The Connect button is
therefore offered regardless of status and the *server* answers — with a session already marked
`AgentUnreachable`, which the remote-control screen explains. Disabling the button on a stale status
would hide working hosts.

**The other two agents need a different design, and Windows now has it.** On macOS the per-user
process holds the fleet identity, which is exactly what remote control needs — the screen and
keyboard belong to a GUI session the root daemon does not have. On Windows the per-user half holds no
identity and makes no network call at all, so it cannot open a socket; and the service, which does
hold the identity, cannot reach a desktop at all because of session 0 isolation. Neither half can do
this alone.

So Windows splits it, and `remote_ipc` is the boundary: the **service** holds both WebSockets and
relays bytes, a **session helper** does the asking, the capturing and the input, and a named pipe
joins them. The rejected alternative was handing the certificate and key to a per-user process so it
could behave like macOS — that would put the fleet private key in the address space of a process
running as whoever is logged in, which is precisely what the identity directory's ACL exists to
prevent.

**The helper runs as SYSTEM inside the user's session, and both halves of that are load-bearing.**
The first cut put this work in the tray process, and it could not answer a UAC prompt — the single
most common thing a support session needs. Two separate Windows rules stood in the way. `SendInput`
cannot reach a window at higher integrity than the sender, so an elevated installer could be watched
and not clicked. And a UAC prompt is drawn on a *different desktop object* (`Winlogon`), which a
thread attached to `Default` can neither read nor write — so the screen froze on the last frame until
somebody answered at the machine.

`session_launcher` therefore duplicates the **service's own** token, moves the copy into the console
session with `SetTokenInformation(TokenSessionId)` — which needs `SE_TCB_NAME`, so only SYSTEM can do
it — and launches the helper with it. `WTSQueryUserToken` is deliberately *not* used to build that
token: it would give the user's own token and a medium-integrity process, back where the tray was.
`remote_desktop` then keeps the capture thread attached to whichever desktop has input, following
Windows onto the secure desktop and back.

**Reading "a SYSTEM process doing input injection" as a step backwards gets it exactly wrong.** It is
a net improvement on three counts. It exists only while a session runs, spawned by the one process
holding this host's identity and only after the server asked. The pipe now has no interactive user on
either end, so its ACL grants Local System and Administrators and **nothing else** — which deletes,
rather than mitigates, the attack the tray design had to reason about, where a local process races to
answer a consent request and feeds the operator a fabricated screen. And the consent dialog is now a
SYSTEM-owned window, so UIPI works in our favour: user-level malware can neither click it nor
suppress it.

**`install.ps1` pinning `obj= LocalSystem` is now load-bearing twice over.** It was already needed so
the service could read the identity directory; it is now also what makes `SE_TCB_NAME` available, and
without it the helper cannot be launched into the session at all. A hardening baseline that moves the
service to a virtual account breaks remote control specifically, and the error names the privilege.

**Thread layout in the helper is dictated by one rule**: `SetThreadDesktop` refuses a thread that
owns any window. So the capture-and-input thread creates none and can follow desktops; the consent
dialog and the session banner each get their own thread and attach once. The capture must also be
**rebuilt** on a desktop switch — its device contexts belong to the desktop they were made on, and a
stale one yields frames of the desktop the user is no longer looking at.

**The indicator moved from the tray to a banner, and that is an upgrade.** A `SYSTEM`-owned bar at
the top of the screen with an "End session" button is visible without being looked for, where a tray
item has to be clicked to be found — and UIPI stops a medium-integrity process clicking it *or
closing it to hide that a session is running*. The tray's remote-session entries were removed rather
than kept alongside, so there is one source of truth about whether a session exists.

**Windows reachability now follows the console session, not a connected process.** With capture in a
helper that exists only during a session, "a tray is connected" is gone as a signal, so the service
asks Windows: `console_session_with_user` is `WTSGetActiveConsoleSessionId` plus a
`WTSQueryUserToken` probe, and the control socket is held open exactly while the answer is yes. A
locked screen still counts, which is correct — the consent dialog appears on the lock screen, and if
nobody is there it times out and is recorded as a timeout.

**One restriction is left on Windows and no process can fix it.** While the secure desktop has input,
only that prompt is usable — that is what a secure desktop is *for*. The consent dialog says so, and
the elevated-window wording it used to carry is gone, because that gap actually closed.

**Windows captures with GDI, not Desktop Duplication, and Remote Desktop is why.** DXGI Desktop
Duplication is the modern API and gives dirty rectangles for free, but `DuplicateOutput` returns
`DXGI_ERROR_UNSUPPORTED` in an RDP session — and a fleet agent is very often reached over RDP, so the
modern API fails precisely on the hosts an administrator is already logged into remotely. `StretchBlt`
from the desktop DC works in both. The costs are named in `screen_capture`: no hardware video
overlays, some layered windows missed, and the cursor has to be drawn in by hand because `BitBlt`
does not include it.

**Linux takes the same split, and two things about it are specific to the platform.**

The first is that it needed **a fourth root unit**. The other three cannot hold a standing
connection — `kintsugi-agent.service` is a oneshot on a timer, `kintsugi-agent-queue.service` is a
oneshot on a path watch, and the per-user unit holds no identity — so remote control runs as
`kintsugi-agent --remote-control` under `kintsugi-agent-remote.service`, resident and
`Restart=always`. It deliberately does **not** take `lock.rs`'s advisory flock: that lock stops two
`apt-get` runs deadlocking on the dpkg lock, this unit installs nothing, and holding it would mean a
remote session blocked patching for as long as somebody was watching.

The second is **two capture and input backends, chosen at session start**, because X11 and Wayland
share nothing here. `backend.rs` picks one; everything downstream — `FrameEncoder`, the tiles,
`remote_ipc`, the server, the viewer — is identical either way.

**Wayland needs a fourth binary, and that is the whole design.** Capture goes through
`xdg-desktop-portal`'s ScreenCast interface, which hands back a PipeWire node — and `libpipewire` is
a C library. The agent links none, which is the only reason CI ships a statically linked musl binary
with no libc floor at all; linking PipeWire would reintroduce that floor for the whole fleet in order
to add remote control on part of it. So the PipeWire half lives in `clients/linux-agent-wayland`,
a separate binary shipped in the same archive and started only for the duration of a session. It
holds no identity, makes no network call and knows nothing about consent — it captures pixels and
injects input, and every security decision stays in the process holding the fleet private key.

**It is started by the per-user process, not the root service, and that is forced.** The portal is
per-user in every respect that matters: `WAYLAND_DISPLAY`, `XDG_RUNTIME_DIR` and
`DBUS_SESSION_BUS_ADDRESS` all name the logged-in user's session, and the portal keys its permission
store by uid. A helper launched by the root service would be asking *root's* compositor for *root's*
grant, and there is not one. `kintsugi-agent-ui.service` already has all three variables from
systemd, so it inherits them by not clearing them — and capture already lived there, so nothing about
the architecture moves.

Raw frames are large (8 MB at 1920x1080) and cross exactly one boundary, the helper's stdout;
encoding stays in the per-user process, so what goes over `remote_ipc` is the same few tens of
kilobytes of JPEG tiles the X11 path sends.

**A Wayland host may be watchable and not drivable, and the viewer has to say so.** Capture and input
are one portal session — `NotifyPointerMotionAbsolute` names the stream it is positioning within, and
the portal only accepts a stream from the same session — but `RemoteDesktop` is optional and
**wlroots does not implement it**, so Sway, Hyprland and river hosts get ScreenCast alone. The
negotiation falls back to capture-only and reports `canControlInput: false`, which is what that flag
on the wire exists for. Without it an operator sees a live picture that ignores the mouse and
concludes the session is broken. Do not "fix" this by writing to `/dev/uinput` as root: that bypasses
the portal's consent entirely, which is the thing the portal exists to enforce.

**The Wayland check runs before the X11 one, and that ordering is the whole point.** Most Wayland
sessions also run XWayland and *do* set `DISPLAY`, so an X11 connection succeeds — and then the root
window is not the compositor's output, so `GetImage` returns black or a desktop containing only X11
clients. A plausible-looking wrong picture is far worse than an error, so `backend::is_wayland_session`
is asked first and `screen_capture::unavailable_reason` is now X11's alone.

**The host user is asked twice, deliberately.** Once by the agent (`dialogs::confirm_remote_control`,
which names the administrator) and once by the portal (which names only the application). Neither can
go: the agent's is the only one that can say *who* is asking, and the portal's is the compositor's own
security boundary. The portal's uses `PersistMode::ExplicitlyRevoked` with a stored restore token so
it is asked once per host rather than once per session; the agent's is asked every time and must
never be persisted.

**That persistence goes on whichever portal created the session, and nowhere else — and getting it
wrong reads as a compositor limitation, not a bug.** On a `RemoteDesktop` session the persist mode
and restore token belong on `SelectDevices`; the `ScreenCast.SelectSources` call made against that
same session must carry neither, because xdg-desktop-portal refuses it outright
(`desktop-portal/screen-cast.c`: `IS_REMOTE_DESKTOP_SESSION` → "Remote desktop sessions cannot
persist"). The helper's first cut sent both on every `SelectSources`, and nothing errored where anyone
looked: the refusal was caught by the view-only fallback, logged as "this compositor's portal does
not offer usable RemoteDesktop", and reported to the viewer as `canControlInput: false` — so every
Wayland host, GNOME and KDE included, showed the "watched but not controlled" notice that is meant
for wlroots. Two things keep it from coming back. `portal::select_sources` takes the persistence
explicitly and the `RemoteDesktop` path passes `None`; and the fallback's log line now states which
call failed and how, rather than asserting a cause. The tokens are two files
(`portal-restore-token-remote-desktop`, `portal-restore-token-screen-cast`) because the portal keeps
them in separate permission tables — one file would lose the remote-desktop grant whenever a session
fell back for a transient reason. When a GNOME or KDE host reports view-only, the journal line above
the agent's "started" entry is the diagnosis; do not start with the compositor.

**Three PipeWire mistakes that each fail silently, all found by running it rather than reading it.**
`clients/linux-agent-wayland/examples/capture-node.rs` streams any PipeWire node with the real
capture module, so the negotiation can be exercised against `gst-launch-1.0 videotestsrc ...
pipewiresink mode=provide` with no compositor involved. It is an example rather than a flag on the
binary because a `--node-id` switch would be a way to point the shipped helper at a stream the portal
never granted. What it caught:

- **Without `StreamFlags::AUTOCONNECT` no link is ever created.** With a target node id it means
  "link to *that* node", not "pick something"; without it the stream sits in `Paused` forever and
  nothing errors. It is the *session manager* that acts on it, so a host with no wireplumber cannot
  capture either.
- **Capping the framerate in the format request breaks negotiation outright.** Asking for a range of
  0/1 to 8/1 reads as the tidy way to want fewer frames, and has no intersection with a producer
  publishing at a fixed 30/1 — the result is `Error("no more input formats")` and a session with no
  picture. Advertise the widest rate that could arrive and drop frames in the process callback, which
  is what `MAX_FRAMES_PER_SECOND` now does. Note there are then **two** rate gates in series and only
  one is authoritative: `DEFAULT_MAX_FPS` in the agent decides the session's rate on both backends,
  and the helper's cap is a bandwidth ceiling deliberately set *above* it. Setting the two equal is
  worse than either — two free-running 8 Hz gates beat against each other and the session runs below
  8 with jitter.
- **`PW_KEY_TARGET_OBJECT` is not where a portal node id goes.** It matches an object *name* or an
  `object.serial`; the portal hands out a global node id, and setting the property to one gives
  `Error("no target node available")`. The deprecated `target_id` argument to `pw_stream_connect` is
  the only thing that takes it.

Two more that are quieter still: the format pod deliberately advertises **no**
`SPA_FORMAT_VIDEO_modifier`, because advertising one lets the compositor hand back DMA-BUF, which
`MAP_BUFFERS` does not map and whose frames would all be silently dropped. And the helper normalises
RGBx/RGBA to BGRA itself rather than putting the pixel format on the wire, because the wire promises
BGRA and a consumer getting that decision wrong produces a sharp picture with the reds and blues
exchanged — which reads as a display-profile problem rather than a byte-order one.

**On Linux a host that cannot be controlled never connects at all.** The per-user process checks
`unavailable_reason` once and, if there is one, never opens the local socket — so the root unit never
opens its control socket and the server reports the host unreachable. That is the same mechanism as
"nobody is logged in", which means there is exactly one way for a host to be unavailable rather than
a session that starts and shows nothing.

**Three Linux-only costs, all named where they are paid.** `GetImage` transfers the whole root window
every frame (~8 MB at 1920x1080) and it is downscaled in software, which is why the frame rate is 8
rather than macOS's 15 — MIT-SHM would avoid the transfer and is deliberately not used, for one code
path rather than two. The consent dialog is zenity or kdialog, and **a host with neither cannot be
remote controlled at all**: no dialog program means consent cannot be asked for, which is the
opposite of what `confirm_patch` does with the same situation, where it proceeds rather than nags.
And kdialog has no way to make No the default button, so its labels are *reversed* — Deny is the Yes
button — which keeps Return and every unexpected exit status on the refusing side.

**A self-update had to learn about the new unit, and the gap it closes would have been invisible.**
`install_binary` replaces only the binary, so a host self-updating from a release that predates
remote control would get the new agent and no unit file to run it under — reporting as unreachable
forever with nothing to explain why, until somebody re-ran `install.sh` on every host.
`self_update::restart_remote_control_unit` therefore installs the unit **if and only if** the path
does not exist, and restarts it otherwise: it is also the one root unit a self-update must restart,
being the only long-running one, or it would go on executing the previous binary until reboot.
Writing units only when absent is what keeps it from ever clobbering a file an administrator edited.

## Platform buckets, and why package managers get their own

`PlatformBucket` keys an `upgrade_paths` row. An AI-researched row lives under an *OS* bucket
(`macOS`, `Windows`, `Linux`); a package-manager-managed row lives under its *manager's* bucket
(`pm:Homebrew`, `pm:App Store`, `pm:winget`, `pm:Chocolatey`, `pm:Flatpak`, `pm:Snap` — see
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
whole fleet. Homebrew, winget, Chocolatey, Flathub, the Snap Store and the Mac App Store (via Apple's
iTunes Search API) each publish one global
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

**An App Store bundle is told apart by its receipt, and reporting it as a plain bundle was actively
harmful.** `Contents/_MASReceipt/receipt` exists in every bundle the Mac App Store installed and in
nothing else — `/System/Applications/*` never carries one. The macOS agent's `read_app_bundle` reports
such a bundle under the `App Store` manager (`system_info::APP_STORE_NAME`, the same string as
`PackageManagerCatalog.AppStore`) with its bundle identifier, and reports the store itself once as
their manager. Before that, an App Store app was a standalone application and went to the AI, whose
macOS prompt assumes a Developer-ID distribution and writes a script that fetches the vendor's DMG
and replaces the bundle — swapping a store build for a direct-download one, receipt and sandbox
container gone, with the store no longer updating it. Signed and approved, that ran as root through
the queue and nothing errored. The receipt also decides what `com.apple.` means: Xcode, Pages,
Keynote, Numbers, iMovie and GarageBand are Apple's *and* sold through the store, and skipping them by
prefix left a Mac with four of them out of date reporting nothing. A VPP-licensed bundle
(`kMDItemAppStoreReceiptIsVPPLicensed`, an MDM's device-based assignment) is reported without an
identifier, because the MDM owns it and no Apple Account can update it.

Two things about `AppStoreUpgradeScript`'s version check fail silently if changed. Its lookup is
`itunes.apple.com/lookup?bundleId=…&entity=desktopSoftware` — **not `macSoftware`**, which for an app
sold as one purchase on iOS and macOS returns the iOS record (Pages 15.3 against a Mac build of
15.3.1; `mas` queries `desktopSoftware` for the same reason). Without `country=` it asks the US
storefront, so an app not sold there answers `resultCount: 0` and the row's `LatestVersion` stays null
— the server cannot know a host's storefront, so this is documented rather than solved.

**An App Store update runs as root, from the daemon, inside the console user's session — the mirror
image of Homebrew.** Since Apple's fix for CVE-2025-43411 (macOS 14.8.2 / 15.7.2 / 26.1) installing a
store update needs root, while starting the download needs the logged-in user's store session:
CommerceKit talks to `com.apple.appstoreagent` in that user's `gui/<uid>` launchd domain, which a bare
root process cannot see (`No bag entry`). The per-user process is one of those two and cannot become
the other — `mas ≥ 4` bridges them by running `sudo installer` itself, and a LaunchAgent has no TTY to
answer it. Root can be both: `launchctl asuser <uid>` puts it inside the user's bootstrap namespace
while it stays uid 0, `mas` — handed `SUDO_UID`/`SUDO_GID` by hand — seteuid's to the user for the
CommerceKit half, and its `sudo installer` asks no password because the real uid is already 0. This
was verified from a real LaunchDaemon on macOS 26.6 (Numbers 15.1 → 15.3.1), *not* from `sudo` in a
Terminal — `sudo` keeps the caller's audit session, and so does `sudo launchctl submit`, which lands
the job in `gui/<uid>` and proves nothing about the daemon; only a plist bootstrapped into the
`system` domain does. So `upgrade::runs_as_root` sends this manager's rows to the root queue by name
(`system_info::APP_STORE_NAME`), the script refuses on its first line if it is not root, and the
`launchctl asuser` dance lives in the script rather than the agent, the way AI-written scripts already
`launchctl asuser … osascript` to quit an application.

**The `mas` it runs is the agent's own root-owned copy, `/usr/local/bin/kintsugi-mas`, and that is not
packaging tidiness.** A root daemon executing Homebrew's `/opt/homebrew/bin/mas` — user-writable — is
root for whoever owns the Homebrew prefix. `publish-release.sh` fetches mas-cli's two per-architecture
`.pkg`s pinned by digest, extracts the Mach-O (`libexec/bin/mas`; `bin/mas` is a zsh formatting
wrapper), `lipo`s them into one universal file, signs it with the fleet identity like the agent, and
refuses to build a single-architecture one; `install.sh` installs it `root:wheel 0755`, `self_update`
replaces it from the same archive whenever one is present (so a host installed before it gains App
Store patching on its next update), and the script checks owner *and* mode before executing it — on
an Intel Mac `/usr/local/bin` is Homebrew's user-owned prefix, so a swapped file there would be
owned by whoever swapped it, which is exactly what the check catches. Bumping `MAS_VERSION` means
re-pinning both digests and re-running the LaunchDaemon check above: mas drives private frameworks
and has broken on macOS majors before; mas 7 needs macOS 13. Two behaviours of `mas` are
load-bearing in the script: it resolves installed apps through Spotlight and re-indexes any it
finds unindexed (noisy, harmless), and a `mas update` with nothing to do **exits 0 having printed
nothing** — so the script treats empty output as failure, because exit 0 is what makes
`patch_cycle::run_patches` report the server's latest version as installed, and a silent no-op would
be a patch result the next inventory contradicts. A store dialog is still possible (an app owned by
a different Apple Account); that is the honest outcome, and nothing here can answer it.

**A signed script is never rewritten by a deployment, and editing one of those bodies changes
nothing until a human says so.** `RegisterApplicationsCommandHandler` used to rewrite `Script` from
the builder on every routine inventory report, under the belief that "the script content for a given
(manager, isSelfUpdate) case never changes". It changes whenever one of those bodies is edited — so
what that actually meant was that a background report could swap the content of a signed row,
content the fleet's agents may be executing right now, on the strength of a deployment nobody was
watching. It is exactly what `UpgradePath.AdoptApprovedScript` refuses to do, and a report has less
business doing it than a human pressing Adopt. Now a row that carries a `ScriptSignature` keeps its
script exactly as reviewed and only `LatestVersion` moves.

**What an unsigned or new row gets is the bucket's reviewed script, not the builder's — and that
rule is `PackageManagerBucketScript`, used by both writers.** Protecting signed rows alone shipped a
second bug: after a builder edit the reviewed rows kept the old text while every row seeded *after*
the deployment — a host installing a new formula, a "Find Upgrade Paths" for one, a force-recheck —
got the new text from the builder, unsigned, because no signature existed for those bytes. The
Upgrade Scripts screen showed two `Homebrew (any managed application)` entries (118 applications and
4), and the 4 were quietly not patching. So `RegisterApplicationsCommandHandler` and
`ResearchApplicationUpgradePathCommandHandler.ApplyPackageManagerCommandAsync` both ask
`IUpgradePathRepository.GetSignedPackageManagerScriptAsync` what the bucket already runs and write
that, signature included; the builder is consulted only for a bucket in which nothing has been
reviewed yet — the very first script per manager, which a human still signs. A package-manager
bucket therefore holds one script, the builder's newer text reaches it only through
`TakeServerWrittenScriptCommand` (which moves every row at once), and a stray unsigned row on a
different text is pulled back into line by the next report. Do not reintroduce a
`packageManager.BuildScript()` call on a write path outside that helper. Signed rows are still never
touched: two *signed* texts in one bucket can only come from a human pasting and then signing a
different script on one row, and that is shown as two entries rather than undone; new rows join the
text on the most rows.

Two things follow. `UpgradePath.Apply` drops `ScriptSignature` whenever the content it is replacing
actually differs (same for `Command`/`CommandSignature`) — the invariant that a signature never
outlives its bytes, which now only ever fires on a deliberate act (a force-refresh, a pasted script,
`TakeServerWrittenScript`) rather than in the background. And because nothing takes the newer script
by itself, the Upgrade Scripts screen has to say one exists: `PackageManagerCatalog.CurrentScriptFor`
gives the query handler the script this build would write, `LocalScriptDto.NewerServerScriptAvailable`
flags a script that differs, and `TakeServerWrittenScriptCommand` replaces it — **unsigned**, so the
new text reaches no host until someone has read it, and one "Sign Script" then covers every row
holding those bytes via `FindExistingSignatureForScriptAsync`. Do not make that automatic on the
grounds that the server trusts its own generated content: the review is the only thing standing
between an edited builder body and root execution on every host.

**The Upgrade Scripts screen lists scripts, not rows.** A package-manager bucket holds one row per
application and the same bytes on every one of them, so listing rows put "firefox", "slack", "zoom"…
under `pm:Homebrew` as hundreds of copies of one decision with nothing per-application on any of
them for a reviewer to look at. `GetUpgradeScriptsOverviewQueryHandler` therefore collapses the rows
of a *recognized* manager's bucket into one `LocalScriptDto` per (bucket, content, signed-or-not),
named the way the approval repository names the same bytes
(`ApprovedScriptIdentity.PackageManagerDisplayName`), with `Applications` saying how many rows it
stands for; an AI-researched row is one application's script and stays its own entry. Content is
part of the key because a bucket legitimately holds two texts at once — rows signed against an older
builder revision beside rows this build wrote — and the review is per text. That is also why
`TakeServerWrittenScriptCommand` is addressed by `(Platform, Sha256)` rather than by row: the button
sits on the entry, and taking the newer text for one application while its siblings kept the old
would leave a bucket running two revisions with nothing to say which was reviewed. A hash that no
longer matches anything is a stale page and answers NotFound rather than acting on whatever
replaced it.

## The three agents

They are deliberately the same program in different clothes: same modules, same names, same
ordering, same comments where the reasoning carries over. Read the macOS one first — it's the
original — then the others for what each platform forced to differ. The differences that matter:

| | macOS | Windows | Linux |
|---|---|---|---|
| Privileged half | root LaunchDaemon, re-invoked by launchd | resident service (`windows-service`) | systemd oneshot on a `.timer` |
| Per-user half | LaunchAgent | logon-triggered task for `BUILTIN\Users` | systemd user unit, `graphical-session.target` |
| Check-in schedule | rewrites its own plist, reloads launchd via a detached helper | computes its next wake in-process | rewrites its own `.timer`, `daemon-reload` |
| Privilege handoff | queue: OS updates, AI-researched scripts and App Store rows; Homebrew stays per-user | queue, everything | queue, everything |
| Inventory | `/Applications` bundles + Homebrew + App Store (by receipt) | uninstall registry (3 views) + winget + Chocolatey | Flatpak + Snap (not dpkg/rpm — see above) |
| OS updates | `softwareupdate` | Windows Update Agent COM API, via PowerShell | apt / dnf / yum / zypper / pacman / apk |
| Host identity | hardware serial, always present | SMBIOS serial, **often a placeholder** | DMI serial, **often a placeholder** |
| Nobody logged in | nothing patches | nothing patches | root service patches unattended — see below |
| Remote control | per-user process, consent + capture + input | service holds the socket, a **SYSTEM session helper** does the rest, named pipe between | resident root unit holds the socket, per-user process does the rest, unix socket between; X11 via XTEST, Wayland via a separate portal/PipeWire binary |
| Choosing a display | restarts the ScreenCaptureKit stream on another `SCDisplay` | a source rectangle of the whole virtual desktop, plus "All displays" | X11: a crop of the root window, plus "All displays". Wayland: whichever outputs the portal's picker granted, re-linked to a different PipeWire node |
| Remote shell | a third root LaunchDaemon, as **root**, asked by the per-user process through its own queue | the service itself, as **SYSTEM**, over ConPTY — no helper | the resident root unit, as **root** — nothing crosses `remote_ipc` |
| Reachable with nobody logged in | no — the per-user process holds the identity | yes, for a shell; a screen request answers `Unavailable` | yes, for a shell; a screen request answers `Unavailable` |

**Linux borrows its architecture from Windows, not macOS, and for the same forcing reason.** Every
upgrade it can perform (`apt-get`, `dnf`, `flatpak update --system`, `snap refresh`) requires root,
so patching lives in the root service and the per-user process holds no identity and makes **no
network call at all** — it decides *when*, and asks. macOS is the odd one out precisely because
Homebrew *refuses* to run as root and installs into a user-writable prefix. The queue directory is
`root:root 1733` (a drop-box: anyone may write, only root may read or list), which is the Linux
spelling of the macOS queue's `root:admin 0770` and needs no group — "local administrators" is
`sudo` on Debian, `wheel` on Red Hat, and neither elsewhere.

**macOS hands off by row, and `upgrade::runs_as_root` is the one place that decision lives.** The
per-user process runs a Homebrew row itself — `brew` refuses to run as root and its installs are
user-owned — and sends an AI-researched row (`UpgradeStatusDto.PackageManager` null) to the root
daemon through the same queue OS updates use. It has to: the prompt tells those scripts to replace
the bundle under `/Applications` or run `installer -pkg ... -target /`, and a bundle that arrived by
MDM or any installer that asked for a password is `root:wheel`. Run as the logged-in user, the
script's `rm` printed `Permission denied` for every file in `Ollama.app` and left the old version in
place, while the log called it a script failure. The queue keeps its one security property in both
directions — an app-patch request carries a name, the daemon re-fetches the row, verifies the
signature and re-asks `runs_as_root` itself, so a forged request can neither run arbitrary code nor
get `brew` run as root — and a request older than `queue::REQUEST_TIMEOUT` or from before the
current boot is discarded unrun, because the process that would have shown progress for it is gone.
The prompt now describes that context (root, a LaunchDaemon, no GUI session — quit the application
via `launchctl asuser`, never relaunch it), so a script is generated for the process that runs it.
An App Store row (`PackageManager` "App Store") goes to the daemon too, for the reason under
"Platform buckets": the install half needs root and the download half needs the console user's
session, and only root can be both.

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

**"Next check-in" is a prediction the per-user process makes from a root-written file, and "Check
In Now" is a queue request.** The menu's check-in line is `checkin_schedule::next_check_in_epoch`:
the next occurrence of the minute in `checkin-schedule.json`, which only the root half writes.
That file therefore has to be readable by the per-user process on all three platforms — root's
`0644` umask into a `0755` directory on macOS, an explicit `0644` in the Linux `persist` (the state
directory is traverse-only, so the mode on the file is all there is, exactly as for `policy.json`),
and `%ProgramData%`'s inherited `Users` read on Windows. It shows "not yet scheduled" until the
first check-in writes it. The scheduler re-reads it every tick and pushes it to the menu only when
it changes, because `format_due` shells out for the local time. "Check In Now" is
`RequestKind::CheckIn`, carrying nothing — the root half re-registers, re-reports the inventory and
runs `self_update`, which is everything it would do at the hour, so the worst a forged request does
is a check-in early. What answers it differs by platform in a way worth knowing before debugging
one: on macOS the file is only a *wake-up* — `WatchPaths` starts the daemon, every daemon
invocation *is* a check-in, and `DaemonRequestHandler::check_in` merely confirms work already done
before the queue was reached — whereas the Windows service (`Agent::check_in`) and the Linux queue
service (`main::check_in`, under the lock it already holds) run the check-in *inside* the request.
If that check-in installs a newer agent, the per-user process asking is restarted before it can
read the answer; the new version in the menu is the confirmation. Both action items are greyed out
while either a patch cycle or a check-in is running, because a cycle owns the schedule state for its
whole length (see below) and the scheduler cannot start a second one while it does.

**A patch cycle runs on its own thread, and the confirmation dialog is why.** All three schedulers
used to call `patch_cycle::run` inline, so the loop was parked inside a dialog that stands there for
a whole delay period — hours. Three things went wrong while it was, and only the first is obvious:
the menu's "Next check-in" line stopped updating; a menu click sat in the channel until the dialog
came down and then ran unasked, which is exactly what the greying above exists to prevent; and on
Linux the per-user heartbeat lapsed after `queue::HEARTBEAT_MAX_AGE` (ten minutes), so the root
service concluded nobody was logged in and **patched unattended under a user who had the prompt
open**. `main::spawn_cycle` hands the cycle its own thread, and hands it the `ScheduleState` by
*ownership* rather than behind a lock — a mutex held for the length of a dialog would have moved the
block rather than removed it, since `is_due` would then be the thing waiting. So `state` being `None`
in the scheduler loop *is* "a cycle is in flight", the join site is where the state comes back, and
`AgentStatus::AwaitingAnswer` is what greys "Patch Now" while a prompt is on screen without opening
the progress window over it. Keep the three copies in step; the shape is identical and only the
arguments a cycle needs differ.

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

**The Windows service cannot restart itself, and the hand-off that does it has to be watched.** A
process that stops its own service is killed by that stop before it reaches the start, so
`self_update` spawns the *newly installed* binary as `kintsugi-agent.exe --restart-service`
(`self_update::run_restart_helper`), which stops the service through the SCM, waits for `Stopped`
for as long as a check-in can take, starts it, and logs every step to `service.log`. It used to be a
detached PowerShell running `Restart-Service -Force -ErrorAction SilentlyContinue`, and that left a
host on 0.5.2 for days with a 0.7.0 binary beside it, its tray (restarted separately, by `schtasks`)
on the new build and every "Check In Now" timing out because a 0.5.2 service does not know that
request kind. Two independent faults, neither of which logged anything: `DETACHED_PROCESS` gives
PowerShell no console and null standard handles, which its console host does not reliably start
under; and `Restart-Service` waits exactly two seconds for `Stopped`, then errors out — never
calling `Start` — unless the service is reporting `StopPending`, which this service's control handler
did not do while its loop polled the shutdown flag every two seconds. Three things now hold. The
control handler in `main.rs` reports `StopPending` (`service::STOP_WAIT_HINT`), which is also what
makes `install.ps1`'s own `Stop-Service` reliable; `self_removal`'s PowerShell helper, which cannot
be the native binary because it deletes it, runs with `CREATE_NO_WINDOW` and `NUL` handles instead.
And the install writes `config::self_update_restart_marker_path()` before handing off, which the
service deletes on every start — so a check-in that finds it is, by construction, the displaced
build still running out of `kintsugi-agent.exe.old`, and re-issues the restart instead of trying to
move a file it is executing. The tell for a host in that state is the hourly
`could not move the running binary aside to ...kintsugi-agent.exe.old: Access is denied` line; hosts
wedged by a release before this one need `Restart-Service KintsugiAgent` by hand.

## Couplings nothing enforces

- nginx's `default.conf` hardcodes the HTTPS redirect port `8443` (both server blocks match any
  host — `server_name _`); nginx config gets no environment substitution, so `8443` must be kept
  in sync with `WEB_TLS_PORT` in `.env` by hand.
- The installer tarball's top-level entry names are load-bearing: `self_update.rs` extracts
  `kintsugi-agent` / `kintsugi-agent.exe` by name out of the same archive a human downloads for a
  fresh install. Both agents publish `.tar.gz` — Windows included — because
  `AgentPackageArchiveRewriter` reads gzip-tar specifically, and `tar.exe` has shipped in Windows
  since 10 1803.
- **The macOS remote-shell handoff is four names that have to agree, and they are checked now.** The
  request suffix (`remote_shell::REQUEST_EXTENSION`), the directory
  (`config::REMOTE_SHELL_QUEUE_DIR`), the job label (`config::REMOTE_SHELL_LAUNCHD_LABEL`) and the
  `WatchPaths` entry plus `ProgramArguments` in
  `packaging/au.com.sharpblue.kintsugiagent-remote-shell.plist` all have to line up, and so does the
  `--remote-shell` arm in `main`. Change one and the per-user process writes a request nothing ever
  reads: the session is reported as never connecting, and neither log says why. Two tests in
  `remote_shell` pin all of it — the suffix, and then the label, the watched directory, the binary
  path and the argument — and they can only do so because the plist is compiled into the binary
  rather than read from the archive. Keep it that way when editing the plist.
- The macOS archive carries **three** plists, not two, and `packaging/install.sh` is now the only
  thing that reads the third out of it — `self_update` installs the job from
  `remote_shell::LAUNCHD_JOB_PLIST` instead (see "Remote control"). Dropping it from the archive
  would leave a fresh install with no remote-shell job until its first check-in repaired it.
- `/usr/local/bin/kintsugi-mas` is named in four places that nothing checks agree: the macOS agent's
  `config::MAS_BINARY_PATH` (what `self_update` replaces and `self_removal` deletes), its
  `MAS_BINARY_NAME` (the tarball entry `self_update` extracts and `publish-release.sh` writes),
  `install.sh`/`uninstall.sh`'s `MAS_DEST`, and the `MAS=` line of the server's
  `AppStoreUpgradeScript`. Move one and App Store rows fail with "kintsugi-mas is not installed" on a
  host that plainly has it — a signed script's text is not rewritten by a deployment, so the server
  side of that rename only reaches a host after a human takes and re-signs the new script.
- **The Windows bootstrap script's pinned hash is coupled to the GitHub asset's bytes and to
  nothing that checks it.** `WindowsBootstrapScript` renders `AgentPackage.UpstreamSha256`, recorded
  when the import fetched that release; if a release is ever re-cut under the same tag, every host
  the script is pushed to refuses to install and the only correct fix is a re-import and a
  re-render. The script is also kept **ASCII-only**, like every other server-written script here,
  because Windows PowerShell 5.1 decodes a BOM-less `.ps1` with the system ANSI code page and this
  is a file an operator saves themselves — a test asserts it. And it names `install.ps1`,
  `config.toml` and `kintsugi-agent.exe` as top-level archive entries, so it is coupled to
  `publish-release.ps1`'s `tar` invocation exactly as `self_update.rs` is.
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
  name). The Windows and Linux agents set it for every managed package; the macOS agent reports the
  Homebrew token for every formula and cask **except** one whose upgrade would make `brew` reach for
  `sudo` (`system_info::cask_requires_root`: a `pkg`/`installer` artifact, or a `pkgutil`, `kext`,
  `script` or `launchctl` uninstall). Leaving the identifier off is deliberately how a row is kept out
  of a patch cycle — the alternative is the Nextcloud loop, where every cycle quits the application,
  fails inside `brew`, and leaves it stopped. Do not "fix" a Homebrew row that never patches by
  adding an identifier server-side.
- **"Is this installation behind" is answered by the agent's package manager first and a version
  comparison second, and the Linux agent is the only one that supplies the first answer.**
  `InstalledApp.update_available` (→ `ApplicationEntry.UpdateAvailable` →
  `InstalledApplication.UpdateAvailable`) is what `flatpak remote-ls --updates` / `snap refresh
  --list` said about exactly this installation; `UpgradePathRepository.InstallationUpdateStatus`
  takes it whenever present and only otherwise compares `LatestVersion` against the installed
  version. It exists because the comparison is wrong for both Linux managers in ways that all read
  as "current": both ship rebuilds under an unchanged version string, and Flatpak prints a version
  in `remote-ls` only when the host's *cached* appstream data holds one — a cache nothing on a fleet
  host refreshes, since the agent is the thing that would run `flatpak update`. Worse, with that
  column empty flatpak's table printer drops the trailing tab too, so the line is the bare
  application id, and a parser requiring two columns (as `parse_flatpak_updates` did) discards the
  update entirely. That is how a Linux host with pending updates showed 0 app updates. Verified
  against Flatpak 1.14.10 in a container: `org.gnome.Calculator` alone before `flatpak update
  --appstream`, `org.gnome.Calculator\t50.0` after. Three things follow. The verdict is
  `Option<bool>` and a failed listing must stay `None` — an empty map would tell the server every
  app is current. A verdict of `true` with no version still seeds the `pm:` row
  (`UpsertPackageManagerUpgradePathsAsync`), or the installation is neither counted nor
  patchable, and it must not erase a `LatestVersion` the row already had from `--update-version`.
  And a `ReportPatchResult` clears the verdict along with recording the new version, or the host
  stays counted as behind until its next inventory report. macOS and Windows leave the field
  `None` on purpose: `brew outdated`, winget's Available column and `choco outdated` only ever name
  a package whose version string changed, so there `available_version` already is the verdict.
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
  do not try to route them through the root queue by having the daemon drive `brew`. The macOS
  agent's `system_info::cask_requires_root` is what keeps them off the patch list, by reporting
  them without an `applicationIdentifier` — see the identifier bullet above.
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
- **The remote-control media protocol is the one hand-mirrored pair with nothing between the two
  ends.** Every other mirrored shape in this repo (the Rust request structs, `web/lib/data/models/`)
  has a C# definition sitting between them, so a mismatch is at least visible in one place. This one
  is agent-to-browser directly — `clients/macos-agent/src/remote_protocol.rs` and
  `web/lib/data/models/remote_control_mapper.dart` — and the server relays the bytes without
  parsing them, so nothing server-side can ever notice. The tile header is big-endian because that
  is `ByteData`'s default on the reading side; get it wrong and the picture still draws, just
  scrambled. `web/test/data/remote_control_mapper_test.dart` asserts the exact bytes the agent's own
  test emits, and is the only check that exists.
- `session_launcher::HELPER_ARGUMENT` and the `--remote-session-helper` arm in `main` are the same
  string in two places. Rename one and the helper starts, fails to recognise its own mode, runs an
  ordinary check-in instead, and the session times out having produced no frame — with nothing in
  either log saying why.
- **The shell half of the media protocol is hand-mirrored the same way the tile half is, and has
  the same nothing between its two ends.** `encode_shell_output`/`decode_shell_input` in each
  agent's `remote_protocol.rs` and `remoteShellOutputFromBytes`/`remoteShellInputToBytes` in
  `web/lib/data/models/remote_control_mapper.dart` are the whole description of it; the server
  relays those bytes without parsing them, so a mismatch is invisible everywhere else. The two
  leading bytes (version, kind) are what keeps a tile from being typed into a shell and a keystroke
  from being drawn as a picture, which is why a shell frame carries them despite needing no
  geometry. `web/test/data/remote_control_mapper_test.dart` asserts the exact bytes the agents'
  own tests emit and accept, and is the only check that exists.
- **Terminal output crosses the wire as bytes and must not be "simplified" to a string.** The agent
  sends whatever the PTY produced when it produced it, so a frame can end mid-codepoint; decoding
  per frame turns any character unlucky enough to straddle a boundary into a replacement mark. The
  viewer holds **one** `Utf8Decoder` for the whole session (`remote_shell_view.dart`) for exactly
  that reason.
- **`pty.rs` is one file twice and a third that only matches its shape.** The macOS and Linux copies
  are byte-identical — both reach a PTY through `openpty`, `setsid` and `TIOCSWINSZ` — and `diff`
  should report nothing. The Windows one is ConPTY and shares no implementation at all; what is kept
  identical is the six-call surface above it (`spawn`, `read_available`, `write_all`, `resize`,
  `try_wait`, `terminate`), because the relay loop that drives it is meant to read the same on all
  three. Its `try_wait` is not redundant with the EOF check beside it: a shell that exits while a
  background process still holds the slave open produces no EOF at all, and without it the session
  sits there attached to nothing.
- The Windows ConPTY path is **compile-checked but never executed** by anything here — the
  docker + mingw + wine arrangement has no console host, so `pty.rs`'s tests there cover the pure
  half only (command-line quoting, the environment block). The Unix agents' `pty.rs` does have live
  tests against a real shell for the behaviour the three share.
- All three agents' `remote_protocol.rs` are copies of one another and must stay so. `diff` any two
  and exactly one line should differ outside the module comment — the cross-reference naming
  `virtual_key_for_hid`, `scan_code_for_hid` or `xtest_keycode_for_hid`, because the three platforms
  reach the same positional key through differently-named APIs. Anything else in that diff is drift,
  and since the server relays the media protocol without parsing it, nothing else would notice.
- **`kintsugi-agent-wayland` is the one binary in this fleet with a libc floor**, and it is the
  price of Wayland support. It links `libpipewire`, so it cannot be the static musl build the agent
  is. CI builds it in a **debian:12** container and asserts the result needs no symbol newer than
  `GLIBC_2.34` (Ubuntu 22.04, RHEL 9, Debian 12, Fedora 35 and up). Going older does not work:
  Ubuntu 22.04's libpipewire is 0.3.48 and `libspa` 0.10 does not compile against those headers at
  all, so **libpipewire 0.3.65 is the floor the crate imposes** and Debian 12 is the oldest
  widely-deployed distribution that has it. A host below either floor is not a broken agent — the
  backend fails to start, the agent reports Wayland capture unavailable with the reason in the
  journal, and X11 hosts, patching and inventory are untouched. Keep that degradation graceful; the
  failure it replaces is a session that connects and never paints.
- The Wayland backend is **optional in the archive**. `publish-release.sh` packages it only if it was
  built (or passed with `--wayland-binary`) and warns loudly when it was not; `install.sh` installs
  it if present; `self_update` installs it beside the agent if the new archive carries one, so a host
  first installed from an X11-only package gains Wayland support on its next update without a
  reinstall. The name lives in `config::WAYLAND_BACKEND_BINARY` because three places have to agree on
  it.
- **The display picker is a third hand-mirrored pair with nothing between its two ends**, alongside
  the tile half and the shell half above. `DisplayOption`/`DisplayInfo.displays` and
  `ViewerInput::SelectDisplay` in each agent's `remote_protocol.rs`, and `RemoteDisplayOption` /
  `RemoteDisplaySelection` in `web/lib/data/models/remote_control_mapper.dart`. Two strings are
  deliberately *not* the same and must stay apart: the geometry message's own `type` is `display`,
  travelling agent-to-browser, while the selection is `select-display`, travelling the other way
  through a different parser. Both sides' tests assert that the wrong one is refused.
- The Wayland helper's own `FormatMessage.node_id` and `DisplayEntry` (`wire.rs`) are mirrored by
  `StreamFormat` and `HelperDisplay` in the agent's `wayland_backend.rs`, the same way the rest of
  that protocol is. A rename on one side alone means a picker that offers nothing — read deliberately
  as non-fatal, so nothing anywhere reports it.
- `x11rb`'s `randr` feature is what `describe_displays` needs for `GetMonitors`. It is pure Rust like
  the rest of x11rb, so it costs the Linux agent's no-C-library invariant nothing — but any *other*
  crate added for display enumeration would break the statically linked musl release for the whole
  fleet, not just remote control.
- `input_injection::evdev_keycode_for_hid` is the base table and `xtest_keycode_for_hid` is that plus
  `EVDEV_KEYCODE_OFFSET`. XTEST wants the offset form; the portal's `NotifyKeyboardKeycode` wants the
  raw kernel code. Getting it backwards types a key eight positions along the physical keyboard —
  wrong letters on Wayland hosts only, which reads as a broken keymap on the host rather than a bug
  in the agent. A test asserts the two agree for every usage.
- The helper's stdio protocol (`clients/linux-agent-wayland/src/wire.rs` and the agent's
  `wayland_backend.rs`) is another hand-mirrored pair, like the Rust structs against the C# DTOs. It
  needs no version negotiation, and only because the two are shipped in one archive and replaced
  together by `self_update` — do not give the helper a separate release cadence without adding one.
- **The Linux agent compiles on macOS, and that is worth not breaking.** It is a Linux program, but
  `cargo build` on a Mac is the fastest way to check a change before waiting on a container — and the
  one place remote control needed a Linux-only facility (`libc::ucred`/`SO_PEERCRED`, for the peer
  check on the local socket) is `#[cfg(target_os = "linux")]` with a stub behind it purely for that
  reason. The stub can never run: only the root unit calls it, and there is no root unit off Linux.
- The Linux agent must keep linking no C library. `x11rb` is pure Rust (its whole tree is
  `rustix`/`linux-raw-sys`) and that is why it was chosen over the `x11`/`libxcb` bindings; the
  check that matters is CI's own, that the musl artifact is not dynamically linked. Adding anything
  that pulls a `-sys` crate here breaks the release for the whole fleet, not just remote control.
- All three `screen_capture.rs` share their whole "pure half" — `FrameEncoder`, `tile_differs`,
  `extract_rgb` and the tile constants — verbatim. They all feed the same viewer, so the tile grid
  and the full-frame threshold have to agree; the platform half above it is the only part that
  differs, and it differs completely (ScreenCaptureKit, GDI, X11 `GetImage`).
- The three agents' tray glyphs (`macos-agent/assets/menu-bar-icon.png`,
  `linux-agent/assets/tray-icon.png`, `windows-agent/assets/tray-icon.png`) are one file three times:
  a black shape on transparency, which each platform recolours itself — macOS as a template image,
  Windows by tinting it black or white for the taskbar's `SystemUsesLightTheme` at load and on every
  `ImmersiveColorSet` broadcast. A replacement that bakes in a colour or a background breaks that on
  all three at once; `cmp` them after touching one.
- `remote_control::CONSENT_TIMEOUT` (60s) must stay *shorter* than
  `RemoteControlDefaults.ConsentTimeout` (90s). The agent's dialog is what should give up, so the
  answer is a reported `TimedOut`; the server's is only a backstop for an agent that never answers
  at all. Invert them and the server abandons a dialog that is still on screen, so a user who then
  clicks Allow grants a session nobody is waiting for.
- `remote_control::CONTROL_SILENCE_TIMEOUT` (90s, all three agents) must stay *longer* than
  `RemoteControlController.KeepAliveInterval` (30s) by a comfortable multiple. The server's ping is
  the only thing an idle control socket ever receives, and it is the agent's only evidence the
  server is still there: a socket whose network went away — a VPN dropping, a laptop waking on a
  different Wi-Fi — never receives a RST, so the kernel reports it `ESTABLISHED` and `read()` returns
  `WouldBlock` forever, exactly as a healthy idle socket does. Before the watchdog a Mac sat like
  that for ten hours, logging "socket open" once and nothing after, "unreachable" on the Hosts
  screen while its check-ins were fine. Set the timeout below the ping interval and every healthy
  host reconnects in a loop instead.
- `/api/remote-control` is gated by its **own `=` location** in `nginx/default.conf`, not by the
  agent regex — the only agent route that is. A new agent route still belongs in the regex; this one
  is separate because a WebSocket needs an hour-long `proxy_read_timeout` that must not apply to
  `/api/host`. The regex's own comment says so, and both need to keep saying it.
- **The code-signing identity's name is one string in five places that nothing checks agree**:
  `packaging/create-signing-identity.sh` (which mints it, as both the PKCS#12's friendly name and
  its CN), `.github/workflows/ci.yml` (which looks the certificate up by it after importing, and
  passes it as `--signing-identity`), `packaging/publish-release.sh` and `packaging/install.sh`
  (which resolve an identity by it), and this list. Rename it in one and the release job fails at
  its own assertions — which is the good outcome, and deliberate: the alternative is a package that
  installs perfectly and orphans every host's Screen Recording and Accessibility grant on the next
  self-update. The two secret names (`MACOS_SIGNING_CERTIFICATE_P12`, `_PASSWORD`) are the same kind
  of pair, shared between that script and that workflow.
- The PPPC profile's `CodeRequirement` is tied to the agent's code signature, which is now stable
  across releases — so the profile is fillable, but with a self-signed leaf rather than the
  Developer ID Apple's guidance assumes, and nobody has confirmed MDM honours that. See
  `packaging/kintsugi-remote-control.mobileconfig.example`, which says what to verify before
  deploying it and what breaks if somebody fills it in from an ad-hoc build anyway.
- `xterm` is a **runtime dependency of the admin UI**, not a dev tool: it is the VT emulator the
  remote terminal is drawn with, and it is pure Dart precisely so it works on web. Dropping it does
  not degrade the terminal, it removes it — what arrives from the agent is escape sequences, and a
  text widget renders them rather than obeying them.
- **The remote terminal takes the keyboard from a `Listener`, in a microtask, and both halves of
  that are load-bearing.** xterm focuses from `onTapDown`, so it needs the press to be recognised as
  a *tap* — and on web a mouse has one logical pixel of slop, so an ordinary click that drifts two
  is a drag, focuses nothing, and leaves a selection behind that the *next* tap is spent clearing.
  That is what made clicking back into a terminal take a random number of attempts. A `Listener`
  does not compete in the gesture arena, so it sees every press however the press is later
  interpreted. The microtask is the other half: a focused text field anywhere on the page unfocuses
  on any pointer down outside itself (`TapRegion`), and that runs *after* an inline handler, so
  focus lands on the route's modal scope and the keyboard goes nowhere. Deferring puts the request
  last, which is the only position that survives.
  `test/presentation/remote_shell_focus_test.dart` pins both, and its gesture must stay
  `PointerDeviceKind.mouse`: a touch pointer tolerates eighteen pixels before a press stops being a
  tap, and the whole bug lives inside that difference — the test sails through against the broken
  code otherwise.
- **The terminal's font family is `AppTheme.monoFamily`, never the name of the face.** `google_fonts`
  fetches Share Tech Mono at runtime and registers it as `ShareTechMono_regular`, keeping the human
  name only as a fallback for an asset-bundled copy that does not exist here — so a widget naming
  `'Share Tech Mono'` matches no registered font, and on web there is nothing to fall back to,
  because Flutter's canvas renderer cannot see Menlo, Consolas or any other system face. The text
  silently lands in the proportional default, which for a terminal breaks every aligned column, box
  drawing and progress bar. Nothing in `flutter analyze` or a release build says a word about it;
  `test/presentation/remote_shell_font_test.dart` is the only check.
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
