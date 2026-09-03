#!/bin/bash
# Stops and removes kintsugi-agent's systemd units and binary.
# Run with sudo:
#   sudo packaging/uninstall.sh
set -euo pipefail

if [[ "$(id -u)" -ne 0 ]]; then
    echo "This script must be run as root (sudo packaging/uninstall.sh)." >&2
    exit 1
fi

BIN_DEST="/usr/local/bin/kintsugi-agent"
CONFIG_DIR="/etc/kintsugi-agent"
STATE_DIR="/var/lib/kintsugi-agent"
SYSTEM_UNIT_DIR="/etc/systemd/system"
USER_UNIT_DIR="/etc/systemd/user"

SERVICE_UNIT="kintsugi-agent.service"
TIMER_UNIT="kintsugi-agent.timer"
QUEUE_SERVICE_UNIT="kintsugi-agent-queue.service"
QUEUE_PATH_UNIT="kintsugi-agent-queue.path"
UI_UNIT="kintsugi-agent-ui.service"
REMOTE_CONTROL_UNIT="kintsugi-agent-remote.service"

# Stop each logged-in user's agent before removing the unit file it runs from — `systemctl stop`
# on a unit whose file has already been deleted still works, but doing it in this order keeps the
# messages honest.
while read -r uid username _; do
    [[ -n "${uid:-}" && "$username" != "root" ]] || continue
    runuser -u "$username" -- env "XDG_RUNTIME_DIR=/run/user/${uid}" systemctl --user stop "$UI_UNIT" 2>/dev/null || true
done < <(loginctl list-users --no-legend 2>/dev/null || true)

# Disable before deleting: `systemctl disable` reads the unit file's [Install] section to work out
# which symlinks to remove, so removing the files first would strand them.
systemctl --global disable "$UI_UNIT" 2>/dev/null || true
systemctl disable --now "$TIMER_UNIT" 2>/dev/null || true
systemctl disable --now "$QUEUE_PATH_UNIT" 2>/dev/null || true
# --now matters more for this one than for the two above: it is resident and Restart=always, so
# without it the process would still be running — and still holding a socket to the server — after
# its binary was deleted.
systemctl disable --now "$REMOTE_CONTROL_UNIT" 2>/dev/null || true
systemctl stop "$SERVICE_UNIT" 2>/dev/null || true
systemctl stop "$QUEUE_SERVICE_UNIT" 2>/dev/null || true

rm -f "${SYSTEM_UNIT_DIR}/${SERVICE_UNIT}" \
      "${SYSTEM_UNIT_DIR}/${TIMER_UNIT}" \
      "${SYSTEM_UNIT_DIR}/${QUEUE_SERVICE_UNIT}" \
      "${SYSTEM_UNIT_DIR}/${QUEUE_PATH_UNIT}" \
      "${SYSTEM_UNIT_DIR}/${REMOTE_CONTROL_UNIT}" \
      "${USER_UNIT_DIR}/${UI_UNIT}"
rm -f "$BIN_DEST"

systemctl daemon-reload

echo "Removed the systemd units and binary."
echo "Config left in place at:          $CONFIG_DIR"
echo "State (identity, queue, logs,"
echo "  policy cache) at:               $STATE_DIR"
echo "  Remove both manually if this host is not coming back — note that deleting the identity"
echo "  means the host has to re-enroll with a current token if it ever is."
echo "Per-user schedule state left in place under each user's"
echo "  ~/.local/state/kintsugi-agent (remove manually if no longer needed)."
