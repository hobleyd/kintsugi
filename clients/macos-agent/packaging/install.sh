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
# The third root job. Its own label rather than a mode of $LABEL because launchd never runs two
# instances of one job, and a remote shell session lasts as long as somebody is typing in it — under
# $LABEL it would stall this host's check-ins, patches and self-update for the length of a support
# call. See src/remote_shell.rs.
REMOTE_SHELL_LABEL="au.com.sharpblue.kintsugiagent-remote-shell"
BIN_DEST="/usr/local/bin/kintsugi-agent"
# The agent's own copy of mas — see src/config.rs MAS_BINARY_PATH and the server's
# AppStoreUpgradeScript, which runs exactly this path as root and refuses it unless root owns it.
MAS_DEST="/usr/local/bin/kintsugi-mas"
PLIST_DEST="/Library/LaunchDaemons/${LABEL}.plist"
UI_PLIST_DEST="/Library/LaunchAgents/${UI_LABEL}.plist"
REMOTE_SHELL_PLIST_DEST="/Library/LaunchDaemons/${REMOTE_SHELL_LABEL}.plist"
CONFIG_DIR="/Library/Application Support/kintsugi-agent"
CONFIG_DEST="${CONFIG_DIR}/config.toml"
QUEUE_DIR="${CONFIG_DIR}/queue"
REMOTE_SHELL_QUEUE_DIR="${CONFIG_DIR}/remote-shell"
IDENTITY_DIR="${CONFIG_DIR}/identity"

# The fleet's code-signing identity, by name — see packaging/create-signing-identity.sh, which
# mints it, and .github/workflows/ci.yml, which is the only thing that holds its key. One of four
# places that have to agree on this string (CLAUDE.md's couplings list is the fourth).
SIGNING_IDENTITY_NAME="Kintsugi Agent Signing"

# Signs a locally built binary with that identity if this machine holds it — which it normally does
# not, since the key is a GitHub Actions secret — and ad hoc otherwise. Only the build-from-source
# path below calls it: a binary out of a published archive is already signed by the release job,
# whose key neither this host nor the person running this script has.
#
# `cargo build` leaves nothing usable behind — the linker signs an arm64 slice as "linker-signed",
# with no designated requirement at all, which TCC will not accept as an identity. So the choice
# here is between a requirement naming the certificate (grants survive every rebuild, which is the
# point of the identity) and one naming this build's hash (grants have to be re-added after every
# `cargo build`, which is what the identity replaced).
#
# Both branches sign as $SUDO_USER, for two separate reasons. The identity lives in *their* login
# keychain, and root's is a different keychain entirely. And `codesign` rewrites the file through a
# `.cstemp` copy it renames into place, so signing as root would leave a root-owned binary in that
# user's target/ directory and their next `cargo build` would fail with Permission denied — which
# is why even the ad-hoc branch, needing no keychain at all, is run the same way.
sign_locally_built_binary() {
    local path="$1" builder="${SUDO_USER:-}"
    # Expanded as ${as_builder[@]+"${as_builder[@]}"} below, not "${as_builder[@]}": macOS ships
    # bash 3.2, where an empty array expanded under `set -u` is an unbound variable and aborts.
    local -a as_builder=()
    [[ -n "$builder" ]] && as_builder=(sudo -u "$builder")

    if [[ -n "$builder" ]] \
            && sudo -u "$builder" security find-identity -v -p codesigning 2>/dev/null \
                | grep -qF "$SIGNING_IDENTITY_NAME"; then
        echo "Signing ${path} with '${SIGNING_IDENTITY_NAME}'..."
        # --timestamp=none: nothing here is notarized, so a timestamp buys nothing and reaching a
        # timestamp authority would make this need the network.
        ${as_builder[@]+"${as_builder[@]}"} codesign --sign "$SIGNING_IDENTITY_NAME" --force \
            --identifier kintsugi-agent --timestamp=none "$path"
    else
        ${as_builder[@]+"${as_builder[@]}"} codesign --sign - --force --identifier kintsugi-agent "$path"
        echo "WARNING: signed ad hoc — a locally built agent has no stable code-signing identity." >&2
        echo "  Its designated requirement is a hash of these exact bytes, so Screen Recording and" >&2
        echo "  Accessibility have to be granted again after every rebuild. That is expected here:" >&2
        echo "  the fleet identity is a GitHub Actions secret, held by the release job that builds" >&2
        echo "  what hosts actually install. Install a release build to keep those grants." >&2
    fi

    codesign --display --requirements - "$path" 2>&1 || true
}

# Says so when the packaged binary carries a per-build requirement, which is the one thing about a
# published archive that decides whether this host keeps its remote-control grants across the next
# self-update. Diagnostic only: there is nothing an installer can do about it, and the symptom
# otherwise is remote control reporting both permissions missing weeks later with the profile and
# the System Settings rows both looking correct.
warn_if_signed_per_build() {
    local path="$1" requirement
    requirement="$(codesign --display --requirements - "$path" 2>&1 || true)"
    if grep -q 'cdhash' <<<"$requirement"; then
        echo "WARNING: ${path##*/} is ad-hoc signed (designated requirement: a hash of this build)." >&2
        echo "  Screen Recording and Accessibility grants for it will not survive this host's next" >&2
        echo "  self-update. A package built by the release job is signed with" >&2
        echo "  '${SIGNING_IDENTITY_NAME}' and does not have that problem; this one was not." >&2
    fi
}

PREBUILT_BIN="$SCRIPT_DIR/kintsugi-agent"
if [[ -f "$PREBUILT_BIN" ]]; then
    echo "Using prebuilt binary at ${PREBUILT_BIN}..."
    SRC_BIN="$PREBUILT_BIN"
    warn_if_signed_per_build "$SRC_BIN"
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
    sign_locally_built_binary "$SRC_BIN"
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

# Optional in the archive (publish-release.sh packages it from mas-cli's release; a hand-built
# source tree has none). Without it the agent still inventories App Store apps and the server still
# tracks their versions; only the upgrade step fails, saying so, until a package that carries it
# arrives by self_update.
PREBUILT_MAS="$SCRIPT_DIR/kintsugi-mas"
if [[ -f "$PREBUILT_MAS" ]]; then
    echo "Installing kintsugi-mas to ${MAS_DEST}..."
    install -o root -g wheel -m 755 "$PREBUILT_MAS" "$MAS_DEST"
    xattr -dr com.apple.quarantine "$MAS_DEST" 2>/dev/null || true
else
    echo "WARNING: no kintsugi-mas beside this script; App Store applications will be inventoried but not upgraded." >&2
fi

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

# Handoff directory for the privileged steps the per-user agent (below) can't do itself: installing
# a macOS software update, and running an application's upgrade script against a root-owned
# /Applications bundle. root:admin 0770 so only an admin console user can drop a request, and only
# root (the daemon) ever acts on one — see src/queue.rs.
echo "Creating queue directory at ${QUEUE_DIR}..."
mkdir -p "$QUEUE_DIR"
chown root:admin "$QUEUE_DIR"
chmod 0770 "$QUEUE_DIR"

# The second handoff directory, for the one privileged step that is not a patch: opening a root
# shell for a remote session the server has already authorised. Same root:admin 0770 as QUEUE_DIR
# and for the same reason — the per-user agent drops a request naming a session id, and only root
# acts on one. Separate from QUEUE_DIR because a separate job watches it; see the note on
# REMOTE_SHELL_LABEL above and src/remote_shell.rs.
echo "Creating remote shell queue directory at ${REMOTE_SHELL_QUEUE_DIR}..."
mkdir -p "$REMOTE_SHELL_QUEUE_DIR"
chown root:admin "$REMOTE_SHELL_QUEUE_DIR"
chmod 0770 "$REMOTE_SHELL_QUEUE_DIR"

# This host's mutual-TLS identity (certificate, private key, pinned CA and artifact-signing
# public key — see src/identity.rs): written once by the root daemon at enrollment, read by both
# the daemon and the per-user agent on every request from then on. Same root:admin 0770 pattern as
# QUEUE_DIR above, so it's readable within the group without being world-readable — the private
# key itself is additionally tightened to 0640 by identity.rs once it's written.
echo "Creating identity directory at ${IDENTITY_DIR}..."
mkdir -p "$IDENTITY_DIR"
chown root:admin "$IDENTITY_DIR"
chmod 0770 "$IDENTITY_DIR"

# The LaunchDaemon plist is the one file the agent owns after installation: its first check-in
# assigns this host a minute and rewrites the plist with a StartCalendarInterval for it
# (src/checkin_schedule.rs), and the packaged copy deliberately has none. Overwriting a plist that
# already carries a schedule would throw that minute away on every reinstall/upgrade — and leave
# the host with nothing but RunAtLoad and WatchPaths until the first check-in after the reinstall
# succeeded, which on a host whose check-in was failing is never. So the packaged copy goes in
# only where no scheduled plist exists; anything else in the template that changed since is picked
# up by the next check-in, which regenerates the whole file whenever it differs from what it would
# write. Same rule as the Linux installer's kintsugi-agent.timer.
# The <key> element, not the bare word: the packaged plist's own comment names the key it lacks.
if grep -q '<key>StartCalendarInterval</key>' "$PLIST_DEST" 2>/dev/null; then
    echo "Keeping the existing LaunchDaemon at ${PLIST_DEST}: it already carries this host's check-in minute."
else
    echo "Installing LaunchDaemon to ${PLIST_DEST}..."
    install -o root -g wheel -m 644 "$SCRIPT_DIR/${LABEL}.plist" "$PLIST_DEST"
fi

# Unload first in case this is a reinstall/upgrade.
launchctl bootout system "$PLIST_DEST" 2>/dev/null || true
launchctl bootstrap system "$PLIST_DEST"
launchctl enable "system/${LABEL}"

# The remote-shell job. Unlike $PLIST_DEST this one is never rewritten by the agent — it carries no
# schedule to preserve — so it is always overwritten with the packaged copy.
echo "Installing remote shell LaunchDaemon to ${REMOTE_SHELL_PLIST_DEST}..."
install -o root -g wheel -m 644 "$SCRIPT_DIR/${REMOTE_SHELL_LABEL}.plist" "$REMOTE_SHELL_PLIST_DEST"

launchctl bootout system "$REMOTE_SHELL_PLIST_DEST" 2>/dev/null || true
launchctl bootstrap system "$REMOTE_SHELL_PLIST_DEST"
launchctl enable "system/${REMOTE_SHELL_LABEL}"

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
