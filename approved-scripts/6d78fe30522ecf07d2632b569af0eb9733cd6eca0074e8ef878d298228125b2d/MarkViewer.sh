#!/bin/bash
set -euo pipefail

# WARNING: Distribution source verified live via GitHub API against
# SeungbinBaik/markviewer-releases on 2026-09-02: latest release was v1.8.5,
# with assets MarkViewer.dmg, MarkViewer_universal.app.tar.gz(+.sig), and
# latest.json. This script uses the .dmg (Developer-ID distribution, not the
# Mac App Store), which is what quarantine-removal below assumes. Only the
# GitHub redirect trick and public API were used for research; no other
# distribution channel (e.g. the vendor's own markviewer.com site) was relied
# on for the update mechanism itself.

REPO="SeungbinBaik/markviewer-releases"
ASSET_NAME="MarkViewer.dmg"

usage() {
  echo "Usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
  exit 1
}

APP_NAME=""
APP_ID=""
MODE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --appName)
      [[ $# -ge 2 ]] || usage
      APP_NAME="$2"
      shift 2
      ;;
    --appId)
      [[ $# -ge 2 ]] || usage
      APP_ID="$2"
      shift 2
      ;;
    --update-version)
      [[ -z "$MODE" ]] || usage
      MODE="update-version"
      shift
      ;;
    --update)
      [[ -z "$MODE" ]] || usage
      MODE="update"
      shift
      ;;
    *)
      usage
      ;;
  esac
done

[[ -n "$APP_NAME" && -n "$APP_ID" && -n "$MODE" ]] || usage

get_latest_version() {
  local redirect_url version
  redirect_url="$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/${REPO}/releases/latest")"
  if [[ -z "$redirect_url" ]]; then
    echo "Error: could not determine latest release (no redirect from releases/latest)" >&2
    return 1
  fi
  version="$(echo "$redirect_url" | sed -n 's#.*/releases/tag/v\{0,1\}\([^/]*\)$#\1#p')"
  if [[ -z "$version" ]]; then
    echo "Error: could not parse version from redirect URL: $redirect_url" >&2
    return 1
  fi
  echo "$version"
}

if [[ "$MODE" == "update-version" ]]; then
  get_latest_version
  exit 0
fi

# --update mode: runs on the managed Mac itself.

APP_PATH="/Applications/${APP_NAME}.app"
INFO_PLIST="${APP_PATH}/Contents/Info.plist"

LATEST_VERSION="$(get_latest_version)"

if [[ ! -d "$APP_PATH" ]]; then
  echo "Error: ${APP_PATH} does not exist" >&2
  exit 1
fi

INSTALLED_ID="$(defaults read "$INFO_PLIST" CFBundleIdentifier 2>/dev/null || true)"
if [[ "$INSTALLED_ID" != "$APP_ID" ]]; then
  echo "Error: installed bundle identifier '${INSTALLED_ID}' does not match expected '${APP_ID}' — refusing to touch ${APP_PATH}" >&2
  exit 1
fi

INSTALLED_VERSION="$(defaults read "$INFO_PLIST" CFBundleShortVersionString 2>/dev/null || true)"
if [[ -z "$INSTALLED_VERSION" ]]; then
  echo "Error: could not determine installed version from ${INFO_PLIST}" >&2
  exit 1
fi

version_ge() {
  # returns success if $1 >= $2, comparing dotted version components numerically
  local a="$1" b="$2"
  local -a av bv
  IFS='.' read -r -a av <<< "$a"
  IFS='.' read -r -a bv <<< "$b"
  local i len
  len=${#av[@]}
  (( ${#bv[@]} > len )) && len=${#bv[@]}
  for (( i=0; i<len; i++ )); do
    local ai="${av[i]:-0}" bi="${bv[i]:-0}"
    ai="${ai//[!0-9]/}"; bi="${bi//[!0-9]/}"
    ai="${ai:-0}"; bi="${bi:-0}"
    if (( ai > bi )); then return 0; fi
    if (( ai < bi )); then return 1; fi
  done
  return 0
}

if version_ge "$INSTALLED_VERSION" "$LATEST_VERSION"; then
  echo "${APP_NAME} is already up to date (installed ${INSTALLED_VERSION}, latest ${LATEST_VERSION})"
  exit 0
fi

echo "Updating ${APP_NAME} from ${INSTALLED_VERSION} to ${LATEST_VERSION}..."

# Quit the app gracefully if it's running, with a bounded grace period before
# falling back to a hard kill.
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

TMP_DIR="$(mktemp -d)"
MOUNT_POINT=""
cleanup() {
  if [[ -n "$MOUNT_POINT" ]]; then
    hdiutil detach -quiet -force "$MOUNT_POINT" >/dev/null 2>&1 || true
  fi
  rm -rf "$TMP_DIR"
}
trap cleanup EXIT

DMG_PATH="${TMP_DIR}/MarkViewer.dmg"
DOWNLOAD_URL="https://github.com/${REPO}/releases/latest/download/${ASSET_NAME}"

if ! curl -fsSL -o "$DMG_PATH" "$DOWNLOAD_URL"; then
  echo "Error: failed to download ${DOWNLOAD_URL}" >&2
  exit 1
fi

MOUNT_POINT="${TMP_DIR}/mnt"
mkdir -p "$MOUNT_POINT"
if ! hdiutil attach -nobrowse -quiet -mountpoint "$MOUNT_POINT" "$DMG_PATH"; then
  echo "Error: failed to mount downloaded disk image" >&2
  exit 1
fi

SRC_APP="$(find "$MOUNT_POINT" -maxdepth 1 -iname '*.app' -print -quit)"
if [[ -z "$SRC_APP" ]]; then
  echo "Error: no .app bundle found in mounted disk image" >&2
  exit 1
fi

rm -rf "$APP_PATH"
cp -R "$SRC_APP" "$APP_PATH"

hdiutil detach -quiet "$MOUNT_POINT" >/dev/null 2>&1 || hdiutil detach -quiet -force "$MOUNT_POINT" >/dev/null 2>&1 || true
MOUNT_POINT=""

xattr -dr com.apple.quarantine "$APP_PATH" 2>/dev/null || true

NEW_VERSION="$(defaults read "$INFO_PLIST" CFBundleShortVersionString 2>/dev/null || true)"
if [[ -z "$NEW_VERSION" ]] || ! version_ge "$NEW_VERSION" "$LATEST_VERSION"; then
  echo "Error: post-install version check failed — installed '${NEW_VERSION:-<none>}', expected at least '${LATEST_VERSION}'" >&2
  exit 1
fi

echo "${APP_NAME} updated to ${NEW_VERSION}"