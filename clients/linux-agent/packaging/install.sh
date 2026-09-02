#!/bin/bash
# Installs kintsugi-agent as a pair of systemd units. Uses the prebuilt "kintsugi-agent" binary
# next to this script if present (e.g. when run from an extracted installer archive); otherwise
# builds from source via cargo (requires Rust, and this script to be run from a repo checkout at
# clients/linux-agent/packaging/install.sh).
#   sudo ./install.sh
#   sudo ./install.sh --enrollment-token <current token>
#   sudo AGENT_ENROLLMENT_TOKEN=<current token> ./install.sh
#
# The enrollment token is a rotating shared secret (see EnrollAgentCommandValidator /
# AGENT_ENROLLMENT_TOKEN on the server) — this installer tarball otherwise has no expiry and gets
# reused across many hosts and a long time, so the token deliberately isn't baked into it. Supply
# whatever the *current* token is at install time via either form above; omitting both falls back
# to whatever's in the packaged config.toml (blank by default), which will fail enrollment with a
# clear "no enrollment token configured" error rather than silently sending a blank one.
set -euo pipefail

ENROLLMENT_TOKEN="${AGENT_ENROLLMENT_TOKEN:-}"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --enrollment-token)
            [[ $# -ge 2 ]] || { echo "--enrollment-token requires a value" >&2; exit 1; }
            ENROLLMENT_TOKEN="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

if [[ "$(id -u)" -ne 0 ]]; then
    echo "This script must be run as root (sudo packaging/install.sh)." >&2
    exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
    echo "This agent is driven entirely by systemd units and cannot run without systemd." >&2
    exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

BIN_DEST="/usr/local/bin/kintsugi-agent"
CONFIG_DIR="/etc/kintsugi-agent"
CONFIG_DEST="${CONFIG_DIR}/config.toml"
STATE_DIR="/var/lib/kintsugi-agent"
QUEUE_DIR="${STATE_DIR}/queue"
IDENTITY_DIR="${STATE_DIR}/identity"
SYSTEM_UNIT_DIR="/etc/systemd/system"
USER_UNIT_DIR="/etc/systemd/user"

SERVICE_UNIT="kintsugi-agent.service"
TIMER_UNIT="kintsugi-agent.timer"
QUEUE_SERVICE_UNIT="kintsugi-agent-queue.service"
QUEUE_PATH_UNIT="kintsugi-agent-queue.path"
UI_UNIT="kintsugi-agent-ui.service"

PREBUILT_BIN="$SCRIPT_DIR/kintsugi-agent"
if [[ -f "$PREBUILT_BIN" ]]; then
    echo "Using prebuilt binary at ${PREBUILT_BIN}..."
    SRC_BIN="$PREBUILT_BIN"
else
    echo "No prebuilt binary found; building from source (release)..."
    # Build as the invoking (non-root) user so cargo's registry/target caches
    # aren't left root-owned; falls back to a plain build if run as real root.
    if [[ -n "${SUDO_USER:-}" ]]; then
        sudo -u "$SUDO_USER" bash -c "cd '$PROJECT_DIR' && cargo build --release"
    else
        (cd "$PROJECT_DIR" && cargo build --release)
    fi
    SRC_BIN="$PROJECT_DIR/target/release/kintsugi-agent"
fi

echo "Installing binary to ${BIN_DEST}..."
install -o root -g root -m 755 "$SRC_BIN" "$BIN_DEST"

echo "Installing config to ${CONFIG_DEST}..."
install -d -o root -g root -m 755 "$CONFIG_DIR"
# Always overwritten, not preserved across reinstalls: this is a centrally-managed fleet agent,
# not something a user configures by hand — the server (via Settings > Patching Policy and
# everything else config.toml doesn't cover) is the single source of truth, and the packaged
# config.toml here is the current source of truth for the little that's left (api_base_url, the
# enrollment token). A stale local override surviving a reinstall would just be a silent way for a
# host to drift from that. The enrollment token itself is one-time-use (see identity.rs) — once a
# host is enrolled, this file being reset to defaults on every reinstall costs nothing.
install -o root -g root -m 644 "$SCRIPT_DIR/config.toml" "$CONFIG_DEST"

if [[ -n "$ENROLLMENT_TOKEN" ]]; then
    # Rewritten via grep+printf rather than sed -i: the token is a secret whose content this
    # script doesn't control, and sed's substitution syntax would break (or need fragile escaping)
    # if it happened to contain the delimiter or a backreference-like sequence. TOML's own escaping
    # only needs backslashes and double quotes handled for a basic string.
    ESCAPED_TOKEN="${ENROLLMENT_TOKEN//\\/\\\\}"
    ESCAPED_TOKEN="${ESCAPED_TOKEN//\"/\\\"}"
    grep -v '^enrollment_token' "$CONFIG_DEST" > "${CONFIG_DEST}.tmp"
    printf 'enrollment_token = "%s"\n' "$ESCAPED_TOKEN" >> "${CONFIG_DEST}.tmp"
    mv "${CONFIG_DEST}.tmp" "$CONFIG_DEST"
    chown root:root "$CONFIG_DEST"
    chmod 644 "$CONFIG_DEST"
    echo "  enrollment token set from the command line/environment."
elif ! grep -q '^enrollment_token = "[^"]' "$CONFIG_DEST" 2>/dev/null; then
    echo "  WARNING: no enrollment token supplied (--enrollment-token / \$AGENT_ENROLLMENT_TOKEN)"
    echo "    and the packaged config.toml's own enrollment_token is blank. Enrollment will fail"
    echo "    until this host's config.toml has the current token — see ${STATE_DIR}/daemon.log."
fi

# Mutable state the root service owns: the enrolled identity, the request queue, the daemon log,
# the check-in schedule, the shared policy cache, and staged scripts.
#
# 0711, not 0700: traverse-only. Root remains the only one who can *list* this directory or read
# the identity inside it, but an unprivileged user must be able to walk *through* it to reach the
# queue below and the policy cache beside it. 0.5.0 used 0700 here, which made the queue's own 1733
# meaningless — no execute bit for others means the traversal fails whatever mode the drop-box
# itself carries, so no user could write a patch request or a heartbeat, and the per-user agent
# reported it as the root service not being installed. See src/config.rs's STATE_DIR_MODE, which
# re-asserts this on every check-in for hosts already installed from a 0.5.0 tarball.
echo "Creating state directory at ${STATE_DIR}..."
install -d -o root -g root -m 711 "$STATE_DIR"

# This host's mutual-TLS identity (certificate, private key, pinned CA and artifact-signing public
# key — see src/identity.rs). Tighter than the macOS agent's equivalent, which has to stay
# group-readable because its per-user process makes authenticated requests directly; here that
# process makes none — it reads the policy from the cache the root service writes and asks that
# service for everything else over the queue — so nothing outside root ever needs to read this.
# Left at 0700 deliberately, and src/config.rs's repair pass never touches it.
echo "Creating identity directory at ${IDENTITY_DIR}..."
install -d -o root -g root -m 700 "$IDENTITY_DIR"

# The privilege handoff between the per-user agent and the root service — see src/queue.rs, which
# explains at length what may and may not cross it. 1733 makes this a drop-box: any logged-in user
# may create a request, nobody but root may list or read the directory, and the sticky bit stops
# one user removing another's request. Deliberately not a group-owned 0770 like the macOS agent's:
# the "local administrators" group is `sudo` on Debian, `wheel` on Red Hat, and neither on plenty
# of distributions, and a drop-box needs no group at all.
echo "Creating request queue at ${QUEUE_DIR}..."
install -d -o root -g root -m 1733 "$QUEUE_DIR"

echo "Installing systemd units..."
for unit in "$SERVICE_UNIT" "$TIMER_UNIT" "$QUEUE_SERVICE_UNIT" "$QUEUE_PATH_UNIT"; do
    install -o root -g root -m 644 "$SCRIPT_DIR/$unit" "${SYSTEM_UNIT_DIR}/$unit"
done
install -d -o root -g root -m 755 "$USER_UNIT_DIR"
install -o root -g root -m 644 "$SCRIPT_DIR/$UI_UNIT" "${USER_UNIT_DIR}/$UI_UNIT"

systemctl daemon-reload

# The timer schedules the hourly check-in; the path unit wakes the queue drain the moment a request
# appears. Enabling with --now also starts them, which for the timer means its OnBootSec run is
# armed from here on rather than only after the next reboot.
systemctl enable --now "$TIMER_UNIT"
systemctl enable --now "$QUEUE_PATH_UNIT"

# --global enables the per-user unit for every user's systemd manager, present and future, without
# needing to know who they are. It starts for each of them when their graphical session comes up.
systemctl --global enable "$UI_UNIT"

# Register straight away rather than waiting for the timer's OnBootSec — the equivalent of the
# macOS agent's RunAtLoad. --no-block because this first run enrolls, inventories, and may patch,
# and there is no reason for the installer to sit through it.
echo "Starting the first check-in..."
systemctl --no-block start "$SERVICE_UNIT"

# For a user already logged in right now, start the per-user agent immediately too, so a
# reinstall/upgrade doesn't require a log out/in to take effect.
while read -r uid username _; do
    [[ -n "${uid:-}" && "$username" != "root" ]] || continue
    echo "Starting the per-user agent for ${username} (uid ${uid})..."
    runuser -u "$username" -- env "XDG_RUNTIME_DIR=/run/user/${uid}" systemctl --user start "$UI_UNIT" 2>/dev/null \
        || echo "  could not start it in ${username}'s session; it will start at their next login."
done < <(loginctl list-users --no-legend 2>/dev/null || true)

echo
echo "Installed. Registration is running now, then hourly at a check-in minute the agent assigns"
echo "itself on this first run."
echo "Root service log:   ${STATE_DIR}/daemon.log"
echo "                     (also: journalctl -u ${SERVICE_UNIT} -u ${QUEUE_SERVICE_UNIT})"
echo "Per-user agent log: ~/.local/state/kintsugi-agent/agent.log"
echo "                     (also: journalctl --user -u ${UI_UNIT})"
echo
echo "On a host with no graphical session, the per-user agent does not run at all and the root"
echo "service patches on the policy's schedule unattended — see src/patch_cycle.rs."
