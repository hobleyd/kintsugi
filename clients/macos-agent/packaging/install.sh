#!/bin/bash
# Installs kintsugi-agent as a LaunchDaemon. Uses the prebuilt
# "kintsugi-agent" binary next to this script if present (e.g. when run from
# an extracted installer archive); otherwise builds from source via cargo
# (requires Rust, and this script to be run from a repo checkout at
# clients/macos-agent/packaging/install.sh).
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

LABEL="au.com.sharpblue.kintsugiagent"
UI_LABEL="au.com.sharpblue.kintsugiagent-ui"
BIN_DEST="/usr/local/bin/kintsugi-agent"
PLIST_DEST="/Library/LaunchDaemons/${LABEL}.plist"
UI_PLIST_DEST="/Library/LaunchAgents/${UI_LABEL}.plist"
CONFIG_DIR="/Library/Application Support/kintsugi-agent"
CONFIG_DEST="${CONFIG_DIR}/config.toml"
QUEUE_DIR="${CONFIG_DIR}/queue"
IDENTITY_DIR="${CONFIG_DIR}/identity"

PREBUILT_BIN="$SCRIPT_DIR/kintsugi-agent"
if [[ -f "$PREBUILT_BIN" ]]; then
    echo "Using prebuilt binary at ${PREBUILT_BIN}..."
    SRC_BIN="$PREBUILT_BIN"
else
    echo "No prebuilt binary found; building from source (release)..."
    # Build as the invoking (non-root) user so cargo's registry/target caches
    # aren't left root-owned; sudo -u fails gracefully if run as plain root.
    if [[ -n "${SUDO_USER:-}" ]]; then
        sudo -u "$SUDO_USER" bash -c "cd '$PROJECT_DIR' && cargo build --release"
    else
        (cd "$PROJECT_DIR" && cargo build --release)
    fi
    SRC_BIN="$PROJECT_DIR/target/release/kintsugi-agent"
fi

echo "Installing binary to ${BIN_DEST}..."
install -o root -g wheel -m 755 "$SRC_BIN" "$BIN_DEST"

# A binary downloaded through a browser (rather than built locally) carries the
# com.apple.quarantine extended attribute. This binary isn't signed/notarized, so a quarantined
# copy is silently blocked by Gatekeeper when launchd tries to run it — it never gets to print so
# much as its first log line, which looks exactly like "nothing happened", especially for the
# menu bar agent (no visible crash dialog, since nothing launched it interactively). Clearing it
# here means the install itself is the one moment this is guaranteed to be dealt with.
xattr -dr com.apple.quarantine "$BIN_DEST" 2>/dev/null || true

echo "Installing config to ${CONFIG_DEST}..."
mkdir -p "$CONFIG_DIR"
chown root:wheel "$CONFIG_DIR"
# Always overwritten, not preserved across reinstalls: this is a centrally-managed fleet agent,
# not something a user configures by hand — the server (via Settings > Patching Policy and
# everything else config.toml doesn't cover) is the single source of truth, and the packaged
# config.toml here is the current source of truth for the little that's left (api_base_url, the
# enrollment token). A stale local override surviving a reinstall would just be a silent way for a
# host to drift from that. The enrollment token itself is one-time-use (see identity.rs) — once a
# host is enrolled, this file being reset to defaults on every reinstall costs nothing.
install -o root -g wheel -m 644 "$SCRIPT_DIR/config.toml" "$CONFIG_DEST"

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
    chown root:wheel "$CONFIG_DEST"
    chmod 644 "$CONFIG_DEST"
    echo "  enrollment token set from the command line/environment."
elif ! grep -q '^enrollment_token = "[^"]' "$CONFIG_DEST" 2>/dev/null; then
    echo "  WARNING: no enrollment token supplied (--enrollment-token / \$AGENT_ENROLLMENT_TOKEN)"
    echo "    and the packaged config.toml's own enrollment_token is blank. Enrollment will fail"
    echo "    until this host's config.toml has the current token — see daemon.log for confirmation."
fi

# Handoff directory for the one privileged step the per-user agent (below) can't do itself:
# installing a macOS software update. root:admin 0770 so only an admin console user can drop a
# request, and only root (the daemon) ever acts on one — see src/os_update.rs.
echo "Creating queue directory at ${QUEUE_DIR}..."
mkdir -p "$QUEUE_DIR"
chown root:admin "$QUEUE_DIR"
chmod 0770 "$QUEUE_DIR"

# This host's mutual-TLS identity (certificate, private key, pinned CA and artifact-signing
# public key — see src/identity.rs): written once by the root daemon at enrollment, read by both
# the daemon and the per-user agent on every request from then on. Same root:admin 0770 pattern as
# QUEUE_DIR above, so it's readable within the group without being world-readable — the private
# key itself is additionally tightened to 0640 by identity.rs once it's written.
echo "Creating identity directory at ${IDENTITY_DIR}..."
mkdir -p "$IDENTITY_DIR"
chown root:admin "$IDENTITY_DIR"
chmod 0770 "$IDENTITY_DIR"

echo "Installing LaunchDaemon to ${PLIST_DEST}..."
install -o root -g wheel -m 644 "$SCRIPT_DIR/${LABEL}.plist" "$PLIST_DEST"

# Unload first in case this is a reinstall/upgrade.
launchctl bootout system "$PLIST_DEST" 2>/dev/null || true
launchctl bootstrap system "$PLIST_DEST"
launchctl enable "system/${LABEL}"

echo "Installing per-user patching LaunchAgent to ${UI_PLIST_DEST}..."
install -o root -g wheel -m 644 "$SCRIPT_DIR/${UI_LABEL}.plist" "$UI_PLIST_DEST"

# /Library/LaunchAgents is auto-loaded for every NEW login session from here on with no further
# action needed. For a user already logged in right now, load it into their session immediately
# too, so a reinstall/upgrade doesn't require a log out/in to take effect.
CONSOLE_USER="$(stat -f '%Su' /dev/console 2>/dev/null || true)"
if [[ -n "$CONSOLE_USER" && "$CONSOLE_USER" != "root" ]]; then
    CONSOLE_UID="$(id -u "$CONSOLE_USER" 2>/dev/null || true)"
    if [[ -n "$CONSOLE_UID" ]]; then
        echo "Loading kintsugi-agent into ${CONSOLE_USER}'s session (uid ${CONSOLE_UID})..."
        launchctl bootout "gui/${CONSOLE_UID}/${UI_LABEL}" 2>/dev/null || true
        launchctl bootstrap "gui/${CONSOLE_UID}" "$UI_PLIST_DEST" 2>/dev/null \
            || echo "  could not load it into the current session automatically; it will start at next login."

        # Give it a moment to start (or crash) before checking, so this is a real health check
        # rather than "the bootstrap call itself didn't error" — those aren't the same thing.
        sleep 2
        JOB_INFO="$(launchctl print "gui/${CONSOLE_UID}/${UI_LABEL}" 2>&1 || true)"
        if [[ -z "$JOB_INFO" ]]; then
            echo "  WARNING: the menu bar agent doesn't appear to be running after loading it."
            echo "    Check /tmp/kintsugi-agent-ui.err.log first (catches even a very early crash);"
            echo "    ~/Library/Application Support/kintsugi-agent/agent.log has more detail once it"
            echo "    gets that far. Or run directly to see errors live: kintsugi-agent --agent"
        elif echo "$JOB_INFO" | grep -q "last exit code = "; then
            LAST_EXIT="$(echo "$JOB_INFO" | grep "last exit code = " | head -1 | sed 's/^[[:space:]]*//')"
            if ! echo "$LAST_EXIT" | grep -qE "last exit code = 0$|last exit code = \(never exited\)$"; then
                echo "  WARNING: the menu bar agent's ${LAST_EXIT}."
                echo "    Check /tmp/kintsugi-agent-ui.err.log and"
                echo "    ~/Library/Application Support/kintsugi-agent/agent.log for why."
            fi
        fi
    fi
else
    echo "No user is currently logged in at the console; kintsugi-agent will start at next login."
fi

echo "Installed and started. Registration runs now (RunAtLoad), then hourly at a check-in minute"
echo "the daemon assigns itself on this first run (see daemon.log)."
echo "Root daemon log:      ${CONFIG_DIR}/daemon.log"
echo "                       (also /var/log/kintsugi-agent.log and .err.log via launchd)"
echo "Per-user agent log:   ~/Library/Application Support/kintsugi-agent/agent.log"
echo "                       (also /tmp/kintsugi-agent-ui.out.log and .err.log via launchd)"
