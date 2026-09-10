# The admin UI (`web/`)

Loaded when Claude reads files under `web/`. The root `CLAUDE.md` carries the rules that apply
before you get here — the `/api/admin/` prefix, `[RequireAdminSession]`, and the nginx location
precedence a new route depends on.

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


## Applications is a menu, and Failed Updates is the second screen under it

`/applications` is still the installed-applications view and **must stay there**: the Hosts screen's
"N app updates" badge and every Vanta record's `externalUrl` deep-link into it with `?status=&host=`
(see `VantaResourceBuilder`, and `.claude/rules/hand-mirrored-dtos.md`, which names that coupling).
So the menu's parent points at it, the way Sync's parent points at Clients, and the sibling is
`/applications/failed`. The parent highlights on `startsWith`, like Hosts.

**The Failed Updates screen is a table plus the *existing* `InstructionsPanel`, and that reuse is the
design.** Repairing a broken script is the same act as researching one — edit the instructions, send
to the AI, review, save, sign — so it is the same widget, with its existing rule that a freshly
generated or hand-edited script is unsigned until a human signs it. The only difference is one
argument: `patchFailureId` reaches `GET /api/upgrade-paths/prompt`, and the *server* appends the
failure's output and the current script to the ordinary research prompt. Composing that text in Dart
instead would put a second author of an AI prompt in the client, free to drift from
`AiUpgradePathResearchClient` — and it is the prompt that carries the `--update-version` / `--update`
CLI contract every agent invokes a script by.

The script sent is whatever the row holds **now**, not a snapshot taken when the failure was
recorded: that is the one an agent would run on its next cycle, so it is the one worth fixing.

**It does not poll.** Nothing here runs in the background on the server — a failure arrives when some
agent's next patch cycle reports one, hours away. The one thing that does change while the screen is
open is an AI repair, and the fix panel polls that itself and then asks the table to reload.

**The default view is outstanding only.** Settled rows (patched since, or dismissed) are kept and
reachable through the status filter rather than deleted, because "this used to fail and then patched"
is the question somebody asks next. A row clears itself when the host reports that application
patched successfully; "Dismiss" is for the ones that cannot recur.


## The remote-control viewer

The rest of remote control — consent, the relay, and the three agents' capture and input — is in
`clients/CLAUDE.md` and the server's `src/CLAUDE.md`. What follows is the viewer's own half. The
media protocol itself is a hand-mirrored contract with the agents and no server-side check; see
`.claude/rules/remote-protocol-mirror.md`, which loads when you open the mapper.

- **The viewer keys the change on `activeDisplayId`, for a narrower reason than it first looks.**
  Every size in a `DisplayInfo` is unchanged across a switch between identical monitors, so that
  field is the only one that moves — and `RemoteControlState` is `Equatable` so a poll finding
  nothing new rebuilds nothing, which means an equal state is never emitted. The *tiles* are safe
  without it, because the bloc clears them on every geometry message and so emits a differing state
  whenever there was a picture. What it protects is the **picker**: an announcement arriving with no
  picture on screen — two in a row, which the Linux backend produces by announcing whenever the
  geometry differs from what it last sent — would be dropped, leaving the dropdown naming the
  display the session had just left and the entry for the one it is on inert when clicked.

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


**A browser cannot forward every keystroke, and that is permanent.** ⌘W, ⌘Q, ⌘T and ⌘Tab are claimed
by the browser and the OS before any page handler runs, so `RemoteKeyCombinations` offers them as
buttons that send an explicit down/up sequence — Force Quit (⌘⌥⎋) most usefully. Keys are sent as
USB HID usages (`PhysicalKeyboardKey.usbHidUsage`) rather than characters, because a virtual keycode
names a *position* and the host applies its own layout: send the character and an administrator on a
US keyboard controlling a French host types the wrong letters.


## Couplings nothing enforces

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

