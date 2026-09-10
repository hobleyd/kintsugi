---
paths:
  - "src/Kintsugi.Application/**"
  - "src/Kintsugi.WebApi/**"
  - "web/lib/data/models/**"
  - "web/lib/domain/entities/enums.dart"
  - "clients/*/src/*.rs"
---

# Couplings: hand-mirrored DTO and enum shapes

A C# command or DTO shape is copied by hand into three Rust agents and into the Flutter client.
Nothing in CI cross-checks them, because the client and the agents are compiled separately.

- Rust request/response structs mirror C# command/DTO shapes by hand with explicit `serde(rename)`
  — changing a command's JSON shape means changing the matching struct in **all three** agents.
- **`RegisterHostRequest` is the exception to "all three", deliberately, and the asymmetry is the
  point.** `operatingSystemId` is sent only by Linux (it is os-release's `ID`, which no other
  platform has) and `operatingSystemVersionId` only by Linux and Windows (VERSION_ID, and the
  build-with-update-revision respectively). macOS sends neither, because `sw_vers` already puts the
  exact version into `operatingSystem`. All three are `Option` + `skip_serializing_if` on the wire
  and nullable in `CreateHostCommand`, so an agent that omits them is not a protocol error — the
  server's `OperatingSystemSubject` reports such a host as one whose operating system it *cannot
  assess*, with the reason, rather than guessing. Do not "tidy" this by having every agent send
  every field: a Windows agent sending an invented `operatingSystemId`, or a server assuming
  revision `.0` for a missing Windows build, both turn "no answer" into a wrong answer — and NVD
  matches Windows CVEs on the revision, so `.0` reports nearly every Windows CVE ever filed
  against a fully patched machine.
- **`RegisterApplicationsCommand.Packages` is a second such exception, and it carries a size
  coupling as well as a shape one.** Only the Linux agent sends it (macOS and Windows have no
  equivalent inventory), and it is `Option`/nullable on both ends so that null means "this agent
  does not report them" while an empty list means "this host has none" — a real difference on a
  mixed fleet, where the wrong reading would let an older agent wipe what a newer one reported
  from the same machine. The size coupling:
  `RegisterApplicationsCommandValidator.MaxPackages` (10000) must stay **above** the Linux
  agent's `system_info::MAX_REPORTED_PACKAGES` (5000), because the packages ride in the same
  request as the application inventory — a report rejected for being one package over the line
  takes that host's applications down with it and leaves the Applications screen quietly wrong,
  with nothing but a log line to say so. Raise the server's figure first, never the agent's. Same
  asymmetry, same direction, as `MaxDetailsLength` against `MAX_REPORTED_FAILURE_BYTES`.
- **The package entries are source-package names, and that is a contract not a convention.** The
  server hands them straight to OSV, which keys on source packages: `libssl3` answers 0
  vulnerabilities on Ubuntu 22.04 where `openssl` answers 48. An agent "helpfully" switched to
  binary names would under-report nearly everything with no error anywhere.
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
