# The backend (`src/`, `tests/`)

Loaded when Claude reads files under `src/`. The root `CLAUDE.md` carries what applies before you
get here: the two-layer agent authentication, `[RequireAdminSession]` on every browser-driven route,
the `/api/admin/` prefix, and the version bump every commit touching `src/` owes.

## Architecture

Layering is conventional (`Domain` ← `Application` ← `Infrastructure` ← `WebApi`) with MediatR
command/query handlers, each feature folder holding a `Command`/`Handler`/`Validator` triad;
FluentValidation runs via `ValidationBehaviour`. What follows is the part no single file explains.

**Two separate key hierarchies, kept apart on purpose.** `CaService` mints agent identities;
`ArtifactSigningService` signs script/command *content*. An AI-generated or hand-pasted script
starts **unsigned** — a human must sign it via `POST /api/upgrade-paths/sign-script`, and the agent
verifies against the signing pubkey it pinned at enrollment before executing anything. Do not make
generation sign automatically.


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
as static files by nginx — see web/CLAUDE.md.


## Remote control: the server's half

The agents' half is in `clients/CLAUDE.md`; the viewer's is in `web/CLAUDE.md`.

**Two auth mechanisms, one on each end, and that is why this is a relay rather than a direct
connection.** The agent's sockets arrive on `/api/remote-control`, inside nginx's exact-match
client-certificate regex, carrying `[RequireAgentIdentity]` so the verified CN must equal the serial
number in the query string. The browser's arrives on `/api/admin/remote-control/...`, outside that
regex and carrying `[RequireAdminSession]`. Neither end could be authenticated by the other's
mechanism, and mutual TLS can only be verified by whatever terminates it — which is nginx. A
peer-to-peer or TURN-relayed design re-terminates somewhere holding no fleet CA, so it would need a
second, parallel auth mechanism *and* an inbound port on every managed Mac. Same constraint as
"The fallback is a guess" in web/CLAUDE.md.


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


**The relay is in-memory and single-process.** A session pairs two sockets that must land in the
same process, so a second API replica behind a load balancer would break remote control specifically
unless both were routed to the same instance. Nothing does that today — compose runs one `api` — but
it is the assumption to check first if that changes.


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
— see "The fallback is a guess" in web/CLAUDE.md. It cannot be derived from the request either,
because the sync normally runs on a timer with nothing in flight. HTTPS is enforced at save time in
the domain entity, because Vanta requires it and the alternative is an opaque rejection a day later.


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


## Verifying a server-written upgrade script

**Verifying it actually works.** `dotnet test` only asserts the shape
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


## One script per package manager

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

