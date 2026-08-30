# Licensing

Copyright (C) 2026 Sharp Blue

This repository contains two separately licensed programs. They communicate
over a network (HTTPS with mutual TLS) and are not linked or combined into a
single work, so each is governed independently by the license below.

## Server — GNU Affero General Public License v3.0

Everything except `clients/`, including:

| Path                  | Contents                                  |
| --------------------- | ----------------------------------------- |
| `src/`                | .NET backend (Domain, Application, Infrastructure, WebApi) |
| `tests/`              | Backend test suite                        |
| `nginx/`              | Reverse-proxy / TLS termination config    |
| `docker-compose.yml`  | Deployment topology                       |

Full text: [`LICENSE`](LICENSE) — SPDX: `AGPL-3.0-or-later`

The AGPL applies here because the server is normally operated as a network
service: anyone who runs a modified version and lets others interact with it
over a network must offer those users the corresponding source.

## macOS client — GNU General Public License v3.0

| Path                   | Contents                        |
| ---------------------- | ------------------------------- |
| `clients/macos-agent/` | Rust macOS agent (daemon + menu-bar UI) |

Full text: [`clients/macos-agent/LICENSE`](clients/macos-agent/LICENSE) — SPDX: `GPL-3.0-or-later`

The agent is distributed to and runs on end-user machines rather than being
operated as a service, so the plain GPL is the appropriate copyleft here.

## Third-party dependencies

Dependencies retain their own licenses; see `clients/macos-agent/Cargo.toml`
and the `*.csproj` files for the dependency sets.
