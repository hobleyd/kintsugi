# nginx

Loaded when Claude reads files under `nginx/`. The rules that decide whether you need to be here at
all are in the root `CLAUDE.md` — a new agent route needs an entry in the exact-match regex, and a
new server-side route needs a `location` above the SPA fallback. Neither is visible from the C#.

The couplings between this configuration and the server live in
`.claude/rules/nginx-routes-and-tls.md`, which loads alongside this file.

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


**nginx's location precedence is the one thing in `default.conf` not to get creative with.** nginx
remembers the longest matching *prefix* and then evaluates regex locations — unless that prefix
carries `^~`, which tells it to stop. So `^~ /api` would become the longest match for `/api/host`,
the agent block's regex would never be consulted, and every agent-only route would be served with no
client certificate at all. The block is a plain `location /api` for that reason. The SPA fallback
(`try_files $uri $uri/ /index.html`) is the last location in the file, so a new server-side route
means adding a location above it or the client answers it with `index.html` — a 200 containing
markup, much harder to diagnose than a 404.

