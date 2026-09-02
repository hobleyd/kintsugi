#!/bin/bash
set -euo pipefail

# WARNING: Inkworm is hosted at github.com/hobleyd/inkworm ("A paginated ePub
# reader; paired with Paladin."). Release tags on that repository are bare
# version numbers (e.g. "2.0.2", no leading "v"), and the macOS asset is
# published under the constant filename "inkworm.dmg" on every release (not
# versioned in the filename), which is what makes the releases/latest/download
# stable-URL pattern usable here. This was confirmed live against the
# repository's actual releases at research time; if the maintainer ever
# renames the asset or switches to a "vX.Y.Z" tag scheme, both --update-version
# and --update's download step will need updating together.

REPO="hobleyd/inkworm"

usage() {
    echo "Usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
    exit 1
}

APP_NAME=""
APP_ID=""
MODE=""

while [ $# -gt 0 ]; do
    case "$1" in
        --appName)
            [ $# -ge 2 ] || usage
            APP_NAME="$2"
            shift 2
            ;;
        --appId)
            [ $# -ge 2 ] || usage
            APP_ID="$2"
            shift 2
            ;;
        --update-version)
            [ -z "$MODE" ] || usage
            MODE="update-version"
            shift
            ;;
        --update)
            [ -z "$MODE" ] || usage
            MODE="update"
            shift
            ;;
        *)
            usage
            ;;
    esac
done

[ -n "$APP_NAME" ] || usage
[ -n "$APP_ID" ] || usage
[ -n "$MODE" ] || usage

get_latest_version() {
    local redirect version
    redirect="$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/${REPO}/releases/latest")"
    if [ -z "$redirect" ]; then
        echo "Error: could not determine latest Inkworm release (no redirect from releases/latest)" >&2
        return 1
    fi
    version="${redirect##*/}"
    if [ -z "$version" ]; then
        echo "Error: could not parse version from redirect URL: $redirect" >&2
        return 1
    fi
    echo "$version"
}

if [ "$MODE" = "update-version" ]; then
    if ! version="$(get_latest_version)"; then
        exit 1
    fi
    echo "$version"
    exit 0
fi

# --update mode: runs on the managed Mac.

if ! LATEST_VERSION="$(get_latest_version)"; then
    exit 1
fi

APP_PATH="/Applications/${APP_NAME}.app"

if [ ! -d "$APP_PATH" ]; then
    echo "Error: ${APP_PATH} does not exist" >&2
    exit 1
fi

INSTALLED_BUNDLE_ID="$(defaults read "${APP_PATH}/Contents/Info" CFBundleIdentifier 2>/dev/null || true)"
if [ "$INSTALLED_BUNDLE_ID" != "$APP_ID" ]; then
    echo "Error: installed bundle identifier '${INSTALLED_BUNDLE_ID}' does not match expected '${APP_ID}'" >&2
    exit 1
fi

INSTALLED_VERSION="$(defaults read "${APP_PATH}/Contents/Info" CFBundleShortVersionString 2>/dev/null || true)"
if [ -z "$INSTALLED_VERSION" ]; then
    echo "Error: could not determine installed version of ${APP_NAME}" >&2
    exit 1
fi

if [ "$INSTALLED_VERSION" = "$LATEST_VERSION" ]; then
    echo "${APP_NAME} is already at the latest version (${INSTALLED_VERSION}); nothing to do."
    exit 0
fi

if [ "$(printf '%s\n' "$INSTALLED_VERSION" "$LATEST_VERSION" | sort -V | tail -n1)" = "$INSTALLED_VERSION" ]; then
    echo "${APP_NAME} installed version (${INSTALLED_VERSION}) is already at or above latest (${LATEST_VERSION}); nothing to do."
    exit 0
fi

# Quit the app gracefully before replacing it, if it's running.
if pgrep -x "$APP_NAME" >/dev/null 2>&1; then
    osascript -e "tell application \"${APP_NAME}\" to quit" >/dev/null 2>&1 || true
    for _ in $(seq 1 15); do
        pgrep -x "$APP_NAME" >/dev/null 2>&1 || break
        sleep 1
    done
    if pgrep -x "$APP_NAME" >/dev/null 2>&1; then
        pkill -x "$APP_NAME" || true
        sleep 1
    fi
fi

WORK_DIR="$(mktemp -d)"
cleanup() {
    if [ -n "${MOUNT_POINT:-}" ] && [ -d "$MOUNT_POINT" ]; then
        hdiutil detach -quiet "$MOUNT_POINT" >/dev/null 2>&1 || true
    fi
    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

DMG_PATH="${WORK_DIR}/inkworm.dmg"
curl -fsSL -o "$DMG_PATH" "https://github.com/${REPO}/releases/latest/download/inkworm.dmg"

MOUNT_POINT="${WORK_DIR}/mnt"
mkdir -p "$MOUNT_POINT"
hdiutil attach -nobrowse -quiet -mountpoint "$MOUNT_POINT" "$DMG_PATH"

SRC_APP="$(find "$MOUNT_POINT" -maxdepth 1 -iname '*.app' -print -quit)"
if [ -z "$SRC_APP" ]; then
    echo "Error: no .app bundle found inside inkworm.dmg" >&2
    exit 1
fi

rm -rf "$APP_PATH"
cp -R "$SRC_APP" "$APP_PATH"

hdiutil detach -quiet "$MOUNT_POINT" >/dev/null 2>&1 || true
MOUNT_POINT=""

xattr -dr com.apple.quarantine "$APP_PATH" 2>/dev/null || true

NEW_VERSION="$(defaults read "${APP_PATH}/Contents/Info" CFBundleShortVersionString 2>/dev/null || true)"
if [ "$(printf '%s\n' "$NEW_VERSION" "$LATEST_VERSION" | sort -V | tail -n1)" != "$NEW_VERSION" ] || [ -z "$NEW_VERSION" ]; then
    echo "Error: after update, installed version (${NEW_VERSION}) does not meet latest (${LATEST_VERSION})" >&2
    exit 1
fi

echo "${APP_NAME} updated to ${NEW_VERSION}."
exit 0