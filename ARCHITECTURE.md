# Architecture: the flows

This file is the *shape* of the system — who talks to whom, in what order, and where a step can
stop. The **reasoning** behind each decision lives in the `CLAUDE.md` beside the code
(`src/`, `web/`, `clients/`, `nginx/`, and each agent's own), and this file names those rather than
restating them. Where a diagram and a `CLAUDE.md` disagree, the `CLAUDE.md` is the one that has
been kept current by the people editing that code.

Six flows are drawn: an agent **enrolling**, an agent **checking in**, an agent **patching**, and
on the server side **client updates**, **script creation and approval**, and **vulnerability
management**. Remote control, the Vanta sync and audit shipping are out of scope here; see
`src/CLAUDE.md` and `clients/CLAUDE.md`.

## The participants

| | |
|---|---|
| **Agent (per-user)** | Menu-bar / tray process. Decides *when* to patch, shows the dialogs. On macOS it holds the fleet identity; on Windows and Linux it holds none and makes no network call. |
| **Agent (root)** | LaunchDaemon (macOS), Windows service, systemd oneshot (Linux). Enrols, checks in, and runs whatever needs privilege. |
| **nginx** | Terminates TLS, verifies the agent's client certificate against the fleet CA, and forwards the verified CN as `X-Agent-Cert-Cn`. Also serves the compiled Flutter bundle. |
| **API** | ASP.NET Core 8, Clean Architecture, MediatR handlers. |
| **Coordinator + background service** | In-memory, one run at a time. Every long server-side job is started by a `POST` and watched by polling a `*-status` route. |
| **Admin UI** | Flutter web, served as static files by nginx. It polls; nothing is pushed to it. |

**Every agent arrow in this file goes through nginx, and that is the authentication.** nginx
requires a client certificate on an *exact-match* regex —
`^/api/(host|applications|patching-policy|upgrade-paths|patch-results|os-patch-results|host-removed)$`
— and `[RequireAgentIdentity]` then compares the forwarded CN against the `serialNumber` in the
request body, so a valid certificate cannot report data for another host. A route missing from that
regex is certless, and nothing in the C# will say so. See the root `CLAUDE.md` and `nginx/CLAUDE.md`.

---

## 1. Enrolment: the one route with no certificate

```mermaid
sequenceDiagram
    autonumber
    participant Root as Agent - root
    participant Nginx as nginx
    participant Api as API
    participant Ca as CaService + ArtifactSigningService

    Note over Root: First run. No identity on disk.
    Root->>Root: generate a keypair and a CSR
    Root->>Nginx: POST /api/host/enroll {serialNumber, csrPem, enrollmentToken}
    Note over Nginx: Outside the client-cert regex, deliberately —<br/>an unenrolled agent has no certificate yet.
    Nginx->>Api: forward
    Api->>Api: constant-time compare of the enrollment token
    alt token does not match
        Api-->>Root: 403 ForbiddenException — nothing is minted
    else token matches
        Api->>Ca: issue a certificate from the CSR, CN = serialNumber
        Ca-->>Api: cert PEM, fleet CA PEM, artifact-signing public key PEM
        Api-->>Root: EnrollAgentResult
        Root->>Root: persist the identity and pin the signing public key
    end
```

Two key hierarchies, kept apart on purpose: `CaService` mints *identities*,
`ArtifactSigningService` signs *content*. The pinned signing key is the only key an agent will ever
verify a script against — which is why an imported approval is re-signed locally rather than
relayed (flow 5c). The serial number **is** the identity: Windows and Linux screen it against a
placeholder list and refuse to enrol rather than invent one. See `src/CLAUDE.md` and
`clients/CLAUDE.md`.

---

## 2. Check-in

Drawn in the macOS shape, which is the only one with two processes and a queue between them. The
platform deltas are tabled below the diagram.

```mermaid
sequenceDiagram
    autonumber
    participant Launchd as launchd
    participant Root as Agent - root daemon
    participant Nginx as nginx
    participant Api as API
    participant Queue as Queue dir

    Launchd->>Root: at boot, hourly at this host's assigned minute, or on WatchPaths
    Root->>Root: repair binary ownership, ensure the remote-shell job
    Root->>Root: checkin_schedule::load_or_assign

    Root->>Root: collect hostname, serial, OS, IP, pending OS updates
    Root->>Nginx: POST /api/host (client cert)
    Nginx->>Api: forward + X-Agent-Cert-Cn
    Api->>Api: RequireAgentIdentity — CN must equal body serialNumber
    Api-->>Root: RegisterHostResponse {removalRequested, suggestedCheckInMinute}

    alt removalRequested
        Root->>Root: self_removal — tear down the jobs
        Root->>Nginx: POST /api/host-removed
        Nginx->>Api: forward
        Api->>Api: hard-delete the soft-deleted row
        Note over Root: Returns here. The plist is gone,<br/>so the schedule is NOT rewritten.
    else normal check-in
        Root->>Root: scan installed applications (bundles, Homebrew, App Store by receipt)
        Root->>Nginx: POST /api/applications {serialNumber, applications}
        Nginx->>Api: forward
        Api->>Api: delete and recreate this host's installed_applications rows
        Api->>Api: seed or update upgrade_paths for recognized package managers
        Api-->>Root: 200

        Root->>Queue: process_queue — drain requests left by the per-user process
        Note over Root,Queue: OS updates, AI-researched rows and App Store rows.<br/>Each request is re-fetched from the server, never trusted.

        Root->>Nginx: GET /api/agent-packages/macos/latest
        Nginx->>Api: forward
        Api-->>Root: version + signed sha256
        Note over Root: self_update — only after everything above succeeded. See flow 4.
    end

    Root->>Root: checkin_schedule::apply(suggested minute or its own)
    Note over Root: The last thing on EVERY exit path, including failure —<br/>the packaged plist carries no schedule until this writes one.
```

Four things that are easy to draw wrong and are load-bearing:

- `suggestedCheckInMinute` comes **back from the server**, which is how check-in load is spread
  across the hour. The agent adopts it.
- `checkin_schedule::apply` runs on the failure path too. A first run that failed used to return
  early and leave the host with nothing to retry it until a reboot.
- `self_update` runs **last**, and only after registration and inventory succeeded.
- The row a `removalRequested` host confirms is soft-deleted, not gone. An agent that cannot
  authenticate can neither learn it should uninstall nor confirm, so the row lingers holding its
  hostname and serial in unique indexes — see `ReclaimHostnameAsync` in `src/CLAUDE.md`.

### What differs per platform

| | macOS | Windows | Linux |
|---|---|---|---|
| Who checks in | root LaunchDaemon, re-invoked by launchd | resident service | systemd oneshot on a `.timer` |
| Who holds the identity | the **per-user** process has it too | root only | root only |
| Patching policy | the per-user process fetches it (`policy::load_or_fetch`) | root fetches on every check-in, writes `policy.json` `0644`; per-user only `load_cached`s | same as Windows |
| Nobody logged in | nothing patches | shell reachable, nothing patches | **the root service patches unattended** when the per-user heartbeat is stale |
| Inventory | bundles + Homebrew + App Store | uninstall registry (3 views) + winget + Chocolatey | Flatpak + Snap as applications; dpkg/rpm reported separately as `packages` |
| Extra OS facts | none — `sw_vers` already exact | `CurrentBuildNumber` + `UBR` | os-release `ID` + `VERSION_ID` |

The macOS per-user fetch is the deliberate exception, and only because that process holds an
identity. Reintroducing a per-user fetch on Windows or Linux reproduces the 0.5.0 bug where every
Linux host with a desktop 403'd once a minute while reporting healthy check-ins — see
`clients/CLAUDE.md`.

---

## 3. Patching

```mermaid
sequenceDiagram
    autonumber
    participant User as Logged-in user
    participant Agent as Agent - per-user
    participant Root as Agent - root
    participant Nginx as nginx
    participant Api as API

    Note over Agent: Poll tick. is_due compares wall clock<br/>against a persisted absolute epoch.
    Agent->>Nginx: GET /api/upgrade-paths?serialNumber=...
    Nginx->>Api: forward + X-Agent-Cert-Cn
    Api->>Api: resolve each row by (application, platform bucket)
    Api-->>Agent: UpgradeStatusDto[] — script, signature, method, packageManager
    Agent->>Agent: is_patchable — updateAvailable AND signature verifies<br/>against the pinned artifact-signing key
    Agent->>Agent: os_update::check_available

    alt nothing patchable
        Agent->>Agent: register_completed, back to Idle
    else work exists
        Agent->>User: confirm dialog — Patch Now / Delay
        alt Delay clicked
            Agent->>Agent: register_delay — a fresh period from now
        else nobody answered
            Agent->>Agent: register_unanswered_prompt — charge the periods that elapsed,<br/>leave the cycle due now
        else Patch Now, or no delays left
            Note over Agent: The no-delays-left dialog IS the five-minute warning —<br/>whatever is left of it is served here.
            loop each patchable application
                alt upgrade::runs_as_root
                    Agent->>Root: queue a request naming the application
                    Root->>Nginx: GET /api/upgrade-paths (re-fetch, do not trust the request)
                    Nginx->>Api: forward
                    Api-->>Root: the row
                    Root->>Root: re-verify the signature, re-ask runs_as_root
                    Root->>Root: run script --appName --appId --update
                    alt succeeded
                        Root->>Nginx: POST /api/patch-results
                    else failed
                        Root->>Nginx: POST /api/patch-failures (tail-truncated to 4000 bytes)
                    end
                    Nginx->>Api: forward
                else runs as the logged-in user (Homebrew)
                    Agent->>Agent: patch_one — re-verify the signature, then run
                    alt succeeded
                        Agent->>Nginx: POST /api/patch-results
                    else failed
                        Agent->>Nginx: POST /api/patch-failures
                    end
                    Nginx->>Api: forward
                end
            end
            opt OS update available
                Agent->>Root: queue an OS-update request
                Root->>Root: softwareupdate / Windows Update / apt, dnf, ...
                Root->>Nginx: POST /api/os-patch-results
                Nginx->>Api: forward
            end
            Agent->>Agent: register_completed, back to Idle
        end
    end
```

- **The signature is verified twice on purpose.** `is_patchable` filters the work list;
  `patch_one` re-verifies immediately before executing. The one function that actually runs
  something is the one that must never skip the check.
- **Only the process that ran the script reports the result.** `runs_as_root` is the branch;
  reporting from both sides would record every root-run failure twice.
- **Only a real execution failure reaches `/api/patch-failures`.** No identity, no signed
  patchable path, the daemon's `runs_as_root` refusal, an unreachable server, an OS update — those
  are configuration problems or have no script to repair, and a queue full of them hides the ones
  the AI can fix.
- A `PatchFailure` is **one row per (host, application)**, folded on repeat with a first-seen date
  and a count. `Platform` is resolved by the server, never sent by the agent.

Where a failure goes next is flow 5: the Failed Updates screen's fix panel appends the captured
output and the current script to the ordinary research prompt, and signing the repair resolves
every outstanding failure for that (application, platform).

---

## 4. Client updates: getting a new agent build onto the fleet

Two halves that meet at `agent_packages`. CI publishes to GitHub Releases and never touches the
server; the server **pulls**.

```mermaid
sequenceDiagram
    autonumber
    participant Ci as GitHub Actions
    participant Gh as GitHub Releases
    participant Ui as Admin UI
    participant Nginx as nginx
    participant Api as API
    participant Sign as ArtifactSigningService
    participant Store as Package storage
    participant Agent as Agent - root

    Note over Ci: A merge to main whose Cargo.toml version<br/>is not already released.
    Ci->>Ci: build per platform, notes from agent-release-notes.sh
    Ci->>Gh: tag and upload macos-agent-v0.5.0.tar.gz etc.

    Ui->>Api: POST /api/admin/clients/refresh (RequireAdminSession)
    Api->>Gh: list releases, latest per platform
    loop each platform not already published at that version
        Api->>Gh: download the archive
        Api->>Api: record the upstream sha256 as provenance
        Api->>Api: rewrite config.toml's api_base_url to this server
        Api->>Store: save the rewritten archive, compute its sha256
        Api->>Sign: sign that sha256
        Sign-->>Api: base64 DER ECDSA-SHA256 signature
        Api->>Api: publish the agent_packages row
    end
    Api-->>Ui: the whole Clients screen state, plus per-platform outcomes

    Note over Agent: Later, at the end of a check-in (flow 2).
    Agent->>Nginx: GET /api/agent-packages/{platform}/latest
    Nginx->>Api: forward
    Api-->>Agent: version, sha256, sha256Signature
    alt version differs from its own
        Agent->>Agent: verify the signed checksum against the pinned signing key
        Agent->>Nginx: GET /api/agent-packages/{platform}/download, presenting its client cert
        Note over Nginx: Outside the exact-match regex, so no cert is REQUIRED —<br/>but ssl_verify_client optional still verifies one that is offered.
        Nginx->>Api: forward + X-Agent-Cert-Verified: SUCCESS
        Note over Api: That header is the whole branch.<br/>SUCCESS returns the archive byte-for-byte.<br/>Anything else gets the enrollment token substituted<br/>into config.toml, which changes the bytes.
        Api-->>Agent: archive
        Agent->>Agent: hash the download, compare against the signed checksum
        Agent->>Agent: extract and install over the running binary
    end
```

- `/api/agent-packages` is anonymous **by design**: a self-updating agent has to see what is
  published before it can prove anything, and the signed checksum is the protection. The
  enrollment-token rewrite is why an anonymous download and an agent download are different bytes,
  and why only the agent's copy can match the signed hash.
- Replacing a running binary differs: macOS and Linux stage beside the target and rename over it;
  Windows renames the *old* image aside, copies in, and deletes the displaced copy at next service
  start, restoring the old one if the copy fails.
- `/api/admin/clients/windows/bootstrap-script` renders a live `AGENT_ENROLLMENT_TOKEN` into a
  silent installer, which is exactly why it sits under `/api/admin/` behind `[RequireAdminSession]`.
- A version bump is half a `Cargo.toml` edit and half a regenerated `Cargo.lock`; CI passes
  `--locked` everywhere, so a bump missing the lock dies before compiling and the tag is silently
  never cut. See `clients/CLAUDE.md`.

---

## 5. Script creation, signing and approval

### 5a. Finding a script

```mermaid
sequenceDiagram
    autonumber
    participant Ui as Admin UI
    participant Api as API
    participant Coord as UpgradePathScanCoordinator
    participant Bg as UpgradePathScanBackgroundService
    participant Ai as AI provider
    participant Db as Database

    Ui->>Api: POST /api/upgrade-paths/scan (RequireAdminSession)
    Api->>Coord: TryRequestStart
    alt already running
        Coord-->>Api: false
        Api-->>Ui: already in progress
    else accepted
        Coord-->>Api: true, releases the semaphore
        Api-->>Ui: StartUpgradePathScanResult — accepted
        Bg->>Coord: WaitForSignalAsync returns
        Bg->>Db: PrepareUpgradePathScan — the work list
        loop each application without a Found row
            alt a recognized package manager owns it
                Bg->>Db: resolve the bucket's already-reviewed script<br/>(PackageManagerBucketScript), builder only if the bucket is empty
                Bg->>Bg: run that script's --update-version on the server
            else standalone application
                Bg->>Ai: one call — research and author the script
                Ai-->>Bg: script + notes
                Bg->>Bg: run its --update-version on the server
            end
            Bg->>Db: upsert the upgrade_paths row, UNSIGNED
            Bg->>Coord: ReportItem
        end
    end
    loop while running
        Ui->>Api: GET /api/upgrade-paths/scan-status
        Api->>Coord: read the counters
        Api-->>Ui: total, completed, resolved, notFound, failed, skipped
    end
```

`POST` + poll a `*-status` route is the shape of **every** long server-side job here: the scan
above, `check-updates`, a single-row `refresh`, and the vulnerability run in flow 6. The UI polls
because the coordinators were built to be polled; there is no push channel. See `web/CLAUDE.md`.

An AI-researched row lives under an OS bucket (`macOS`, `Windows`, `Linux`); a package-manager row
lives under its manager's bucket (`pm:Homebrew`, `pm:winget`, ...), because what a `brew upgrade`
row depends on is the manager, not the OS. Every builder returns **byte-identical** content for
every application — the name and id arrive as `--appName`/`--appId` at runtime — which is what lets
one review cover every application a manager handles.

### 5b. Signing, and what a signature reaches

```mermaid
sequenceDiagram
    autonumber
    participant Human as Administrator
    participant Ui as Admin UI
    participant Api as API
    participant Sign as ArtifactSigningService
    participant Db as Database
    participant Gh as Approval repository

    Note over Human: A generated or hand-pasted script is UNSIGNED.<br/>No agent will run it. Generation never signs.
    opt hand-edited
        Ui->>Api: POST /api/upgrade-paths/save (RequireAdminSession)
        Api->>Db: store the script, drop any signature the bytes no longer match
    end

    Human->>Ui: read the script, press Sign Script
    Ui->>Api: POST /api/upgrade-paths/sign-script {applicationName, platform, patchFailureId?}
    Api->>Sign: sign the script bytes
    Sign-->>Api: signature
    Api->>Db: store it on this row
    Api->>Db: propagate to every UNSIGNED sibling row holding the same bytes
    opt patchFailureId was supplied (the Failed Updates screen)
        Api->>Db: resolve every outstanding failure for that (application, platform) as ScriptRepaired
    end
    Api->>Db: SaveChangesAsync

    Note over Api,Gh: Only after the save. Every failure below is reported, never thrown —<br/>a GitHub outage must not stop a reviewed script patching the fleet it was reviewed for.
    Api->>Gh: does approved-scripts/{sha256}/metadata.json exist on the default branch?
    alt it exists
        Gh-->>Api: yes
        Api-->>Ui: AlreadyApproved — nothing written
    else new content
        Api->>Gh: open a pull request adding the script,<br/>metadata.json and signatures/{fingerprint}.json
        Gh-->>Api: pull request URL
        Api-->>Ui: the URL, shown beside the row
    end
```

- **The pull request is a record and a distribution channel, not a gate.** The signature is
  effective locally the moment it is saved — the human at the console reviewed it.
- **The default branch is the trust root, and nothing else is.** The signer's public key travels in
  the same repository as the script it vouches for, so verifying an entry proves it is internally
  consistent and *names* its signer, not that the signer was authorized. Branch protection on that
  branch is the only real control. The UI says so; keep it saying so.
- Layout is content-addressed because a package-manager script is byte-identical across every
  application that manager handles, so one review covers all of them; one signature file per signer
  means two servers approving the same bytes never conflict.

### 5c. Taking an approval from another server

```mermaid
sequenceDiagram
    autonumber
    participant Ui as Admin UI
    participant Api as API
    participant Gh as Approval repository
    participant Sign as ArtifactSigningService
    participant Db as Database

    Ui->>Api: POST /api/admin/upgrade-scripts/refresh
    Api->>Gh: read approved-scripts/** from the default branch
    loop each entry
        Api->>Api: verify the entry against the public key it carries
        alt this server already has a row on those exact bytes, unsigned
            Api->>Sign: re-sign the same bytes with the LOCAL key
            Sign-->>Api: signature
            Api->>Db: bless the row — automatic
        else content this server does not have
            Api-->>Ui: offer it for Adopt, with the signer's fingerprint beside it
        end
    end
    opt a human presses Adopt
        Ui->>Api: POST /api/admin/upgrade-scripts/adopt
        Api->>Api: refuse if the row already carries a signature (agents may be running it)
        Api->>Sign: sign locally
        Api->>Db: write the script and the local signature
    end
```

**A remote signature is never served to an agent.** Each agent pinned exactly one signing key at
enrolment — its own server's — so the importing server re-signs the same bytes locally. That is why
this feature needed no change in any of the three agents.

Blessing is automatic because no new content arrives; adoption is a button because a merge to that
repository is enough to *offer* new executable content to every server that refreshes.
`TakeServerWrittenScript` — for when a deployment's builder text differs from what a bucket runs —
writes the new text **unsigned** for the same reason.

---

## 6. Vulnerability management

One bounded, resumable run. Five stages, each committing before the next, and **a failing stage does
not abort the run** — they fail for unrelated reasons, and an unconfigured AI provider must not stop
a KEV refresh that needs no AI.

```mermaid
sequenceDiagram
    autonumber
    participant Ui as Admin UI
    participant Api as API
    participant Coord as VulnerabilityRunCoordinator
    participant Bg as VulnerabilityAssessmentBackgroundService
    participant Kev as CISA KEV
    participant Ai as AI provider
    participant Nvd as NVD
    participant Osv as OSV
    participant Db as Database

    alt an administrator presses Run
        Ui->>Api: POST /api/admin/vulnerabilities/run (RequireAdminSession)
        Api->>Coord: TryRequestStart — one run at a time
        Api-->>Ui: 202 accepted, or 409 if one is already running
    else the schedule comes round
        Bg->>Coord: TryStartScheduledRun, every SyncIntervalHours<br/>after a two-minute startup delay
    end
    Bg->>Coord: WaitForSignalAsync returns

    rect rgba(128, 128, 128, 0.12)
    Note over Bg,Kev: Stage 1 — KEV refresh
    Bg->>Kev: fetch the Known Exploited Vulnerabilities catalogue
    Bg->>Db: ApplyKevEntry on each CVE, withdraw flags CISA no longer lists
    Note over Bg,Db: A failed fetch withdraws nothing —<br/>the download failed is not the same fact as CISA delisted this.
    Bg->>Db: commit
    end

    rect rgba(128, 128, 128, 0.12)
    Note over Bg,Db: Stage 2 — discovery
    Bg->>Db: every distinct installed application name becomes a CpeMapping, Unmapped
    Bg->>Db: every host's OS facts become an OperatingSystemSubject mapping
    Bg->>Db: queue a CpeAssessment per (confirmed mapping, distinct installed version)
    Bg->>Db: commit
    end

    rect rgba(128, 128, 128, 0.12)
    Note over Bg,Nvd: Stage 3 — suggestion, only when AutoSuggestCpes is on
    loop up to 25 Unmapped mappings
        Bg->>Ai: what vendor and product tokens does NVD index this under?
        Ai-->>Bg: vendor, product — a SEARCH TERM, never a claim about vulnerability
        Bg->>Nvd: CpeExistsAsync on the proposed match string
        alt NVD's dictionary does not contain it
            Bg->>Bg: discard the proposal
        else it exists
            Bg->>Db: record it as Suggested — still NOT assessed
        end
    end
    Bg->>Db: commit
    end

    rect rgba(128, 128, 128, 0.12)
    Note over Bg,Nvd: Stage 4 — application and OS assessment
    Bg->>Db: take the least recently assessed pairs, up to AssessmentsPerRun
    loop each (confirmed mapping, version)
        Bg->>Nvd: virtualMatchString = the CPE name carrying that concrete version
        Note over Nvd: NVD evaluates the version ranges.<br/>Nothing here parses versionStartIncluding.
        Nvd-->>Bg: the CVEs whose configurations cover it
        Bg->>Db: upsert vulnerabilities, replace this assessment's matches
        Bg->>Db: stamp LastAssessedUtc — EVEN ON FAILURE, so a rejected pair<br/>cannot park at the head of the queue
        Bg->>Db: commit this pair
    end
    end

    rect rgba(128, 128, 128, 0.12)
    Note over Bg,Osv: Stage 5 — Linux distribution packages
    Bg->>Db: queue a PackageAssessment per (ecosystem, source package, version)
    loop batches, ONE ECOSYSTEM PER BATCH
        Bg->>Osv: query by {name, ecosystem}, never by purl
        Note over Osv: An unrecognized ecosystem fails the WHOLE batch with 400,<br/>which is why batches are not mixed.
        Osv-->>Bg: advisories
        Bg->>Bg: resolve USN-/RLSA- style ids to CVEs, cached forever, the no-CVE answer included
        Bg->>Db: upsert, replace matches, commit
    end
    end

    Bg->>Coord: done, with the count still remaining
    loop while running
        Ui->>Api: GET /api/admin/vulnerabilities/run
        Api-->>Ui: stage counters, problems, assessmentsRemaining
    end
```

### The human step in the middle

Nothing is assessed until a person confirms the mapping. The queue lives on the Vulnerabilities
screen because that is where an empty one gets fixed.

```mermaid
sequenceDiagram
    autonumber
    participant Human as Reviewer
    participant Ui as Admin UI
    participant Api as API
    participant Nvd as NVD

    Human->>Ui: open CVE Mapping (/vulnerabilities/mapping)
    opt search by hand
        Ui->>Api: GET /api/admin/vulnerabilities/cpe-dictionary?q=...
        Note over Api,Nvd: Its own nginx location with a 180s read timeout —<br/>the general /api block's 60s would turn a correct<br/>31-second rate-limit wait into a 504.
        Api->>Nvd: keyword search
        Nvd-->>Api: candidates, ranked by NVD, not by us
        Api-->>Ui: candidates — the screen says why these are candidates and not an answer
    end
    Human->>Ui: Confirm, or Not applicable
    Ui->>Api: POST /api/admin/vulnerabilities/mappings/{id}/confirm
    Api->>Nvd: CpeExistsAsync AGAIN — a reviewer can type a correction, and a typo<br/>attributes another product's CVEs to this one
    alt NVD does not have it
        Api-->>Ui: rejected
    else confirmed
        Api-->>Ui: confirmed — assessed on the next run
    end
```

### Facts the diagrams above depend on

- **NVD evaluates the version ranges.** Handing `virtualMatchString` a CPE name carrying a concrete
  version returns only the CVEs whose configurations cover it. Re-implementing that matching locally
  would be a second, divergent opinion about which versions a CVE affects.
- **Linux distribution packages go to OSV, never to NVD's CPE ranges.** Distributions backport
  security fixes without changing the upstream version, so a CPE match on a dpkg version reports
  CVEs fixed months ago. The name queried is the **source** package (`openssl`, not `libssl3`).
- **A package needs no mapping queue.** "slack" could be Slackware; a distribution's own source
  package name is unambiguous, so packages produce findings the moment they are reported. A
  Linux-only fleet therefore has real coverage with zero confirmed mappings.
- **A KEV entry cannot tell you whether you are affected** — CISA's catalogue has no version ranges
  at all, so `KnownExploited` is only ever an overlay on a match NVD's ranges already produced.
- **`NvdRateLimiter` is a singleton because the limit belongs to the server**, not to a component.
  NVD counts per source address and answers an overrun with a 403, which reads like an auth failure.
- **Coverage this feature does not have is a first-class number.** `UnmappedSubjectCount` and
  `UnassessableHostCount` ride beside the findings, and an empty table distinguishes "nothing found"
  from "nothing looked at". Windows refuses to assess without the update revision rather than
  assuming `.0`, because `10.0.22631.4317` answers 1355 CVEs where `.6000` answers 793.

---

## Where to read next

| Flow | Owning file |
|---|---|
| Layering, auth gap, buckets, approval, vulnerability assessment | `src/CLAUDE.md` |
| Polling, tables, enums on the wire, the Vulnerabilities screens | `web/CLAUDE.md` |
| Queue, check-in scheduling, patch cycles, per-platform differences | `clients/CLAUDE.md` and each agent's own |
| Location precedence, client-certificate verification, 495/496 | `nginx/CLAUDE.md` |
| Couplings with two ends in different directories | `.claude/rules/` |
