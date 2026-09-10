---
paths:
  - "nginx/**"
  - "src/Kintsugi.WebApi/**"
  - "docker-compose.yml"
---

# Couplings: nginx, routes and TLS

These are entries from CLAUDE.md's "Couplings nothing enforces" whose two ends are nginx's
configuration and the server. Nothing checks that they agree.

- nginx's `default.conf` hardcodes the HTTPS redirect port `8443` (both server blocks match any
  host — `server_name _`); nginx config gets no environment substitution, so `8443` must be kept
  in sync with `WEB_TLS_PORT` in `.env` by hand.
- A new server-side route needs a `location` in `nginx/default.conf` *above* the SPA fallback, or
  nginx answers it with `index.html` — a 200 containing markup rather than a 404, which is
  considerably harder to diagnose. The fallback is deliberately the last block in the file.
- `/api/remote-control` is gated by its **own `=` location** in `nginx/default.conf`, not by the
  agent regex — the only agent route that is. A new agent route still belongs in the regex; this one
  is separate because a WebSocket needs an hour-long `proxy_read_timeout` that must not apply to
  `/api/host`. The regex's own comment says so, and both need to keep saying it.
- **nginx loads the fleet CA's public certificate at startup and exits without it, so the API has
  to create that file before the first agent exists.** `Program.cs` calls
  `EnsureAgentFleetCaExists` for exactly this reason. `CaService` generates the CA lazily, on the
  first `GetCaCertificatePem`/`IssueClientCertificatePem` — which is to say from
  `EnrollAgentCommandHandler`, on the first enrollment — and an enrollment has to arrive through
  nginx. Without that startup call a clean deployment deadlocks: `docker compose up` reports the
  api service healthy and nginx in a restart loop, complaining about a missing certificate nothing
  was ever going to write. Do not make the CA lazy again on the grounds that nothing needs it
  until an agent turns up.
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
- Volumes that must survive a redeploy: `dataprotection-keys` (or every session is signed out),
  `agent-ca-private` / `agent-ca-public` (or the whole fleet must re-enroll), `agent-packages`,
  `db-data`.
