---
paths:
  - "clients/*/src/remote_control.rs"
  - "src/Kintsugi.Application/**/RemoteControl*"
  - "src/Kintsugi.WebApi/**/RemoteControl*"
  - "src/Kintsugi.Domain/**/RemoteControl*"
---

# Couplings: remote-control timeout orderings

Four timeouts are split across the three agents and the server, and each ordering is load-bearing in
a direction that is easy to invert. Each agent keeps its own copy of the server's 30s as
`SERVER_PAIRING_TIMEOUT`; that copy is the coupling, and nothing checks it still matches the server.

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
- `remote_control::CONSENT_FLUSH_TIMEOUT` plus `SESSION_CONNECT_ATTEMPTS` × `SESSION_CONNECT_TIMEOUT`
  (plus the retry delays) must stay *inside* `RemoteControlSessionBroker.RemoteControlPairingTimeout`
  (30s), in all three agents. The agent has to run out of attempts before the server runs out of
  patience: an agent still retrying when the relay gives up reports nothing, so its own message —
  which names the address and the failure — is replaced by the server's "the other end never
  connected", which names neither the host nor the reason. Each agent keeps its own copy of the
  server's 30s as `SERVER_PAIRING_TIMEOUT` and a test asserts the sum against it; that copy is the
  coupling, and nothing checks it still matches the server.
