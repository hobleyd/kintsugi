# Licensing

Copyright (C) 2026 Sharp Blue

This repository contains a server and the clients that report to it. They are
separately licensed programs: they communicate over a network (HTTPS with
mutual TLS) and are not linked or combined into a single work, so each is
governed independently by the license named for it below.

## Server — GNU Affero General Public License v3.0

Everything except `clients/`, including:

| Path                  | Contents                                  |
| --------------------- | ----------------------------------------- |
| `src/`                | .NET backend (Domain, Application, Infrastructure, WebApi) |
| `tests/`              | Backend test suite                        |
| `nginx/`              | Reverse-proxy / TLS termination config    |
| `web/`                | Flutter web admin UI (served by nginx)    |
| `docker-compose.yml`  | Deployment topology                       |

Full text: [`LICENSE`](LICENSE) — SPDX: `AGPL-3.0-or-later`

The AGPL applies here because the server is normally operated as a network
service: anyone who runs a modified version and lets others interact with it
over a network must offer those users the corresponding source.

## Clients — GNU General Public License v3.0

Each agent is its own crate, versioned and released independently of the
others, so each carries its own copy of the license text:

| Path                            | Contents                                | Full text |
| ------------------------------- | --------------------------------------- | --------- |
| `clients/macos-agent/`          | Rust macOS agent (daemon + menu-bar UI) | [`clients/macos-agent/LICENSE`](clients/macos-agent/LICENSE) |
| `clients/windows-agent/`        | Rust Windows agent (service + tray UI)  | [`clients/windows-agent/LICENSE`](clients/windows-agent/LICENSE) |
| `clients/linux-agent/`          | Rust Linux agent (units + tray UI)      | [`clients/linux-agent/LICENSE`](clients/linux-agent/LICENSE) |
| `clients/linux-agent-wayland/`  | Wayland capture/input backend for the Linux agent | [`clients/linux-agent-wayland/LICENSE`](clients/linux-agent-wayland/LICENSE) |

SPDX for all four: `GPL-3.0-or-later`

The agents are distributed to and run on end-user machines rather than being
operated as a service, so the plain GPL is the appropriate copyleft here.

## Third-party dependencies

Dependencies retain their own licenses; see each agent's `Cargo.toml` under
`clients/` and the `*.csproj` files for the dependency sets.
