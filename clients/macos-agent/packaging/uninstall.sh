#!/bin/bash
# Stops and removes kintsugi-agent's LaunchDaemon, binary, and config.
# Run with sudo:
#   sudo packaging/uninstall.sh
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
    echo "This script must be run as root (sudo packaging/uninstall.sh)." >&2
    exit 1
fi

LABEL="au.com.sharpblue.kintsugiagent"
UI_LABEL="au.com.sharpblue.kintsugiagent-ui"
PLIST_DEST="/Library/LaunchDaemons/${LABEL}.plist"
UI_PLIST_DEST="/Library/LaunchAgents/${UI_LABEL}.plist"
BIN_DEST="/usr/local/bin/kintsugi-agent"
CONFIG_DIR="/Library/Application Support/kintsugi-agent"

launchctl bootout system "$PLIST_DEST" 2>/dev/null || true

CONSOLE_USER="$(stat -f '%Su' /dev/console 2>/dev/null || true)"
if [[ -n "$CONSOLE_USER" && "$CONSOLE_USER" != "root" ]]; then
    CONSOLE_UID="$(id -u "$CONSOLE_USER" 2>/dev/null || true)"
    [[ -n "$CONSOLE_UID" ]] && launchctl bootout "gui/${CONSOLE_UID}/${UI_LABEL}" 2>/dev/null || true
fi

rm -f "$PLIST_DEST"
rm -f "$UI_PLIST_DEST"
rm -f "$BIN_DEST"
rm -f /var/log/kintsugi-agent.log /var/log/kintsugi-agent.err.log
rm -f /tmp/kintsugi-agent-ui.out.log /tmp/kintsugi-agent-ui.err.log

echo "Removed LaunchDaemon, LaunchAgent, binary, and logs."
echo "Config left in place at: $CONFIG_DIR (remove manually if no longer needed)."
echo "Per-user schedule/policy state left in place under each user's ~/Library/Application"
echo "  Support/kintsugi-agent (remove manually if no longer needed)."
