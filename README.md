# Kintsugi

Enterprise patch management for a mixed fleet of macOS, Windows and Linux machines.

An agent on each host enrolls itself, reports what is installed, and runs signed upgrade scripts
unattended. The server keeps one upgrade path per (application, platform), researches the ones it
does not know with an AI provider, and lets a human review and sign the resulting script before any
host executes it. An administrator can also watch a host's screen or open a terminal on it from the
browser.

This README is how to stand it up and use it. **[CLAUDE.md](CLAUDE.md) is the reasoning** behind
the constraints below — every "this fails quietly" note here has a longer explanation there.

---

## What it does

**Inventory.** Each agent reports its installed applications on a schedule: `/Applications`
bundles, Homebrew formulae and casks, and Mac App Store apps on macOS; the uninstall registry,
winget and Chocolatey on Windows; Flatpak and Snap on Linux. Pending OS updates are reported too
(`softwareupdate`, the Windows Update Agent, `apt`/`dnf`/`zypper`/`pacman`/`apk`).

**Upgrade paths.** An application managed by a recognized package manager gets that manager's
script — one script per manager, shared by every application it handles. Anything else is
researched by an AI provider, which writes a durable upgrade script rather than answering a
question. Scripts are bash on macOS and Linux, PowerShell on Windows.

**Human approval.** A generated or pasted script starts **unsigned** and no agent will run it. A
person reviews it on the Upgrade Scripts screen and presses Sign Script; the server signs the
content with a key that never leaves its volume, and each agent verifies against the signing key it
pinned at enrollment. Signing can also open a pull request against a shared approval repository, so
one review can be picked up by other Kintsugi servers.

**Patching.** A central patching policy decides how often a host patches and how many times a user
may delay. On macOS and Windows the logged-in user is asked and can defer; on Linux, servers with
nobody logged in patch unattended.

**Remote control.** Watch a host's screen (keyboard and mouse included) or open a root/SYSTEM
terminal on it, relayed through the server — no inbound port on any managed machine. A screen
session always asks the person at the keyboard first, and every request is recorded, refusals
included.

**Compliance and audit.** Optional Vanta sync pushes the fleet's out-of-date applications as
vulnerability evidence. Settings > Auditing configures where audit events are shipped (Datadog,
Loki, Cloud Logging, CloudWatch, Azure Monitor, Splunk HEC, or generic HTTP).

## How it is put together

| Piece | What it is |
|---|---|
| `src/` | ASP.NET Core 8 API, Clean Architecture (`Domain` ← `Application` ← `Infrastructure` ← `WebApi`) with MediatR handlers |
| `web/` | Flutter web admin UI, Clean Architecture + BLoC — compiled into the nginx image and served as static files |
| `nginx/` | The only ingress. Terminates TLS, serves the UI, verifies each agent's client certificate |
| `clients/macos-agent`, `clients/windows-agent`, `clients/linux-agent` | The three Rust agents — the same program in different clothes |
| `clients/linux-agent-wayland` | The Linux agent's Wayland capture helper, a separate binary because it links libpipewire |
| PostgreSQL | Reachable only from the API, on an egress-less internal network |

Agents authenticate with **mutual TLS**: a client certificate issued by a fleet CA the server
generates on first run, checked by nginx, with the verified Subject CN compared against the serial
number in every request body. The browser authenticates with an OIDC sign-in cookie. Neither end
could use the other's mechanism, which is why remote control is a relay through the server rather
than a direct connection.

---

## Standing it up

### Prerequisites

* Docker with Compose. That is the whole runtime requirement — .NET, Flutter and Rust are only
  needed to work on the code.
* A **publicly-trusted** TLS certificate and key for the hostname agents and browsers will use.
* A host reachable by every managed machine on the port you publish for TLS.

### 1. TLS material

nginx loads its certificate at startup and exits without it, so this comes first:

```bash
mkdir -p nginx/tls
cp /path/to/fullchain.pem nginx/tls/fullchain.pem
cp /path/to/privkey.pem   nginx/tls/privkey.pem
```

`nginx/tls/` is gitignored and never baked into an image.

Two requirements on that chain, both of which fail in ways that are easy to misdiagnose:

* **It must be publicly trusted.** Agents validate it against the host OS trust store with no way
  to pin or except anything, so a self-signed certificate stops the entire fleet at the handshake.
* **It must be complete.** Agents do no AIA chasing, so a missing intermediate fails with
  `invalid peer certificate: UnknownIssuer` — while `curl` and browsers succeed, because they fetch
  the missing link themselves. Verify with the count, not with curl:

  ```bash
  echo Q | openssl s_client -connect kintsugi.example.com:8443 -servername kintsugi.example.com -showcerts 2>/dev/null \
      | grep -c 'BEGIN CERTIFICATE'
  ```

Whoever renews that certificate has to copy the new pair here and reload nginx. If a proxy in front
used to own renewal, it no longer does — the fleet goes dark on expiry day.

### 2. Configuration

```bash
cp .env.example .env
```

`.env` is gitignored. Every entry is documented in the file itself; two are **required** and
compose refuses to start without them:

* `POSTGRES_PASSWORD`
* `AGENT_ENROLLMENT_TOKEN` — the one-time secret a new agent presents to enroll. Generate one with
  `openssl rand -hex 32`. Rotating it later does not affect already-enrolled agents.

Two more are worth getting right before the first agent installs:

* **`WEB_TLS_PORT`** is published to the host, and it is also hardcoded as `8443` in
  `nginx/default.conf` — nginx config gets no environment substitution, so changing one means
  changing the other by hand.
* **`AGENT_API_BASE_URL`** is baked into every agent package the server publishes. It must name
  **nginx's own address and `WEB_TLS_PORT`**, which is not the address you browse the admin UI on
  whenever anything terminates TLS in front of nginx — a gateway, a load balancer, a CDN. nginx is
  what verifies the agent's client certificate, and a hop that ends the TLS handshake at itself
  cannot pass that certificate on. Point an agent at the wrong door and it enrolls, looks
  installed, and 403s on every authenticated route forever. Left blank, the Clients screen falls
  back to the address it was reached on and says on screen that it is guessing.

  For a deployment where something else already owns 443, `nginx/edge-sni-router.conf.example`
  documents the only arrangement that works: an `ssl_preread` stream server that hands the agent
  hostname's bytes through untouched.

The GitHub entries near the bottom of `.env.example` are legacy. A fresh deployment should leave
them blank and use Settings > GitHub instead.

### 3. Run it

```bash
docker compose up -d --build
```

This is the only supported way to run the system, the API and the UI alike. `dotnet run` will not
work — the API hardcodes container paths — and the admin UI is a compiled bundle that
`nginx/Dockerfile` builds, so it does not exist until the image does.

The first build downloads the Flutter SDK and compiles the bundle. On Apple Silicon that stage runs
under emulation (Flutter publishes no arm64 Linux SDK) and takes several minutes; it looks hung and
is not. Later builds reuse the layer unless `web/pubspec.*` changes.

Check it came up:

```bash
docker compose ps
curl -fsS https://kintsugi.example.com:8443/health
```

### 4. First sign-in

Browse to `https://kintsugi.example.com:8443/`.

**A fresh deployment pins you to Settings > Authentication and nothing else is reachable** until
that screen is saved. That is deliberate, not a broken deploy: no administrator has yet decided
whether sign-in is required, so everything is closed until one has. Choose a provider — Google
Workspace, Microsoft Entra, Clerk, or generic OIDC — and fill in the client id and secret. Sign-in
is server-side, so the provider needs a confidential (web application) client; the screen gives the
redirect URI to register.

You can save that screen with authentication disabled to try the system out. Do not leave a
reachable deployment that way: the whole admin UI, including the routes that sign scripts every
host runs as root, is then open to anyone who can reach it.

### 5. Configure the rest

The remaining settings screens are alphabetical in the sidebar and independent of each other:

* **AI Agent** — which provider researches unknown upgrade paths: Anthropic API, OpenAI, Ollama,
  Goose, or the Claude Agent SDK (the `claude` CLI, which spends a Claude subscription's included
  usage rather than metered API credits). Nothing is researched until one is configured.
* **GitHub** — the read-only token that lifts GitHub's anonymous rate limit, which repository agent
  builds are pulled from, and the repository approved scripts are shared through, with its own
  separate write token.
* **Patching Policy** — how often hosts patch, and how many times a user may delay.
* **Auditing** — where audit events are shipped.
* **Vanta** — off until switched on; pushes the fleet as compliance evidence.

### Volumes that must survive a redeploy

| Volume | Lose it and |
|---|---|
| `db-data` | you lose everything |
| `agent-ca-private` / `agent-ca-public` | every agent in the fleet must re-enroll |
| `dataprotection-keys` | every browser session is signed out |
| `agent-packages` | published agent builds are gone until re-imported |

---

## Getting agents onto hosts

1. Open **Sync > Clients** and press **Refresh clients**. The server checks the configured GitHub
   repository's releases, downloads anything newer, rewrites `api_base_url` to this server's own
   address, and republishes it locally. The screen shows each platform's current version and the
   release notes for every newer build.
2. Download the platform's archive from that screen. The download substitutes the *current*
   enrollment token into the packaged `config.toml`, so a package downloaded here needs no token on
   the command line.
3. Extract it on the host and run its installer elevated:

   ```bash
   # macOS and Linux
   sudo ./install.sh
   sudo ./install.sh --enrollment-token <current token>   # if the archive came from elsewhere
   ```

   ```powershell
   # Windows, from an elevated PowerShell session
   .\install.ps1
   .\install.ps1 -EnrollmentToken '<current token>'
   ```

   Each installer uses the prebuilt binary beside it, or builds from source with cargo if there
   isn't one.

The host appears on the Hosts screen after its first check-in and reports its applications shortly
after. Removing a host from that screen is a *request*: the row disappears at once, and the agent
uninstalls itself the next time it checks in.

**A CrowdStrike-managed Windows estate** can use the Windows row's **Deploy Script** button
instead: a silent PowerShell installer, rendered by the server with this deployment's address, the
current enrollment token and a pinned SHA-256 of the GitHub release it downloads. Pushing it through
CrowdStrike is what makes that pin worth something — the hash arrives over a separately
authenticated channel from the download it is checking.

### If a host will not authenticate

* **403 on every route but enrollment** — the agent is pointed at something that terminates TLS in
  front of nginx. Fix `AGENT_API_BASE_URL`, re-import the packages, reinstall.
* **"certificate rejected"** — the fleet CA was regenerated under an already-enrolled agent. Delete
  the host's identity directory outright (not its contents) and let it re-enroll.
* **Windows or Linux host refuses to enroll at all** — its SMBIOS serial number is a placeholder
  like `To Be Filled By O.E.M.`. The serial *is* the host's identity, so the agent refuses rather
  than inventing one; fix it in firmware.

---

## Day to day

* **Hosts** — the fleet, with last check-in, OS, and per-host actions: Connect (watch the screen),
  Terminal (a shell), and remove.
* **Applications** — every installed application across the fleet, grouped under its package
  manager, with how many hosts have it and how many are behind. **Find Upgrade Paths** resolves an
  update method for everything that has not got one; **Check for Updates** re-runs each resolved
  script's own version check. Expanding a row shows its script, and **Sign Script** is there.
* **Sync > Upgrade Scripts** — every script in one list, one entry per distinct script rather than
  per row, with who signed it. Where a newer server-written script is taken and re-signed, and
  where approvals are refreshed from the shared repository.
* **Sync > Clients** — agent builds, per platform.

Nothing an agent runs is executed unsigned. Generation never signs; a human does.

---

## Working on the code

The full development notes are in [CLAUDE.md](CLAUDE.md) — including how to build the Windows agent
from a Mac, how to exercise the Wayland capture backend without a compositor, and the couplings
nothing enforces. The commands:

```bash
# Backend (needs the .NET SDK 8 — every csproj targets net8.0)
dotnet build Kintsugi.sln
dotnet test tests/Kintsugi.Tests/Kintsugi.Tests.csproj

# EF Core migrations. dotnet-ef is a *local* tool, pinned to 8.0.10.
dotnet tool restore
dotnet ef migrations add <Name> --project src/Kintsugi.Infrastructure --startup-project src/Kintsugi.WebApi

# Admin UI
cd web && flutter analyze
cd web && flutter test
cd web && flutter run -d chrome     # needs a running `docker compose` for its API calls

# Agents (Windows only builds on Windows; Linux builds natively or in a container)
cd clients/macos-agent   && cargo test
cd clients/windows-agent && cargo test
cd clients/linux-agent   && cargo test
```

`dotnet ef` resolves its connection string from `appsettings.json`, whose value only resolves inside
compose — override `ConnectionStrings__Database` when running it from the host.

Agents are released by CI: bump `version` in the agent's `Cargo.toml`, **regenerate its
`Cargo.lock`** (CI passes `--locked` and a stale lock file fails before the tests even run), and
merge to `main`. CI tags a GitHub Release per agent whose version is not already released; the
server pulls it from the Clients screen.

Bump `<Version>` in `src/Kintsugi.WebApi/Kintsugi.WebApi.csproj` in every commit that touches `src/`
or `web/`. It is the version the admin UI's sidebar shows, and the only way to tell a current
deployment from a stale one.

---

## Security notes worth knowing before you deploy

* **This repository is public.** No deployment detail belongs in a tracked file — not just
  credentials but the server's own address. Secrets live in `.env`, TLS material in `nginx/tls/`,
  both gitignored, and every agent default ships `kintsugi.example.com`.
* **Two key hierarchies, kept apart.** One CA mints agent identities; a separate key signs script
  content. Neither is ever exported.
* **A signature proves who signed, not that they were authorized.** For scripts shared through the
  approval repository, the branch protection on its default branch is the only real control — a
  merge there is enough to offer new executable content to every server that refreshes, which is
  why adopting a remote script is a button a human presses rather than something that happens
  automatically.
* **The relay is in-memory and single-process.** Remote control pairs two sockets that must land in
  the same process, so a second API replica behind a load balancer would break it.
* `/swagger` is reachable without signing in — route disclosure only, but worth knowing.
