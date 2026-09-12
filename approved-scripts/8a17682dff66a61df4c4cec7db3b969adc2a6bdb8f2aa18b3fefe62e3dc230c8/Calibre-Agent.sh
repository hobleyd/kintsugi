#!/bin/bash
set -euo pipefail

# WARNING: Calibre Agent (au.com.sharpblue.calibreagent) is the Flutter-based
# desktop app at https://github.com/hobleyd/calibre_agent. Verified via the
# GitHub releases API on 2026-09-12 that its last three releases (1.0.0+1,
# 1.0.1, 1.0.2) all ship a macOS asset named exactly "calibre-agent.dmg"
# (unversioned), so the stable /releases/latest/download/ URL is used below.
# If a future release renames or drops this asset, the download step will
# fail loudly (curl -f) rather than silently doing the wrong thing. The
# process name used for pgrep/pkill is read from the installed bundle's
# CFBundleExecutable rather than assumed, since Flutter apps' executable name
# does not always match the display name used in --appName.

GH_OWNER="hobleyd"
GH_REPO="calibre_agent"
GH_ASSET="calibre-agent.dmg"

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
      MODE="version"
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
  redirect_url=$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/${GH_OWNER}/${GH_REPO}/releases/latest" 2>/dev/null) || return 1
  [[ -n "$redirect_url" ]] || return 1
  version="${redirect_url##*/}"
  [[ -n "$version" && "$version" != "latest" ]] || return 1
  printf '%s' "$version"
}

version_ge() {
  [[ "$1" == "$2" ]] && return 0
  [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n1)" == "$1" ]]
}

if [[ "$MODE" == "version" ]]; then
  latest=$(get_latest_version) || {
    echo "Error: unable to determine latest version of ${APP_NAME} from GitHub releases" >&2
    exit 1
  }
  printf '%s\n' "$latest"
  exit 0
fi

# --update mode: runs on the managed Mac itself, as root, outside any GUI session.

APP_BUNDLE="/Applications/${APP_NAME}.app"
INFO_PLIST="${APP_BUNDLE}/Contents/Info.plist"

if [[ ! -f "$INFO_PLIST" ]]; then
  echo "Error: ${APP_BUNDLE} not found or missing Info.plist" >&2
  exit 1
fi

installed_id=$(defaults read "$INFO_PLIST" CFBundleIdentifier 2>/dev/null) || {
  echo "Error: unable to read CFBundleIdentifier from ${INFO_PLIST}" >&2
  exit 1
}

if [[ "$installed_id" != "$APP_ID" ]]; then
  echo "Error: installed bundle identifier '${installed_id}' does not match expected '${APP_ID}' - refusing to touch the wrong app" >&2
  exit 1
fi

installed_version=$(defaults read "$INFO_PLIST" CFBundleShortVersionString 2>/dev/null) || {
  echo "Error: unable to read CFBundleShortVersionString from ${INFO_PLIST}" >&2
  exit 1
}

latest_version=$(get_latest_version) || {
  echo "Error: unable to determine latest version of ${APP_NAME} from GitHub releases" >&2
  exit 1
}

if version_ge "$installed_version" "$latest_version"; then
  echo "${APP_NAME} is already up to date (installed ${installed_version}, latest ${latest_version})"
  exit 0
fi

echo "Updating ${APP_NAME} from ${installed_version} to ${latest_version}..."

executable_name=$(defaults read "$INFO_PLIST" CFBundleExecutable 2>/dev/null || true)
[[ -n "$executable_name" ]] || executable_name="$APP_NAME"

if pgrep -x "$executable_name" >/dev/null 2>&1; then
  console_uid=$(stat -f %u /dev/console)
  launchctl asuser "$console_uid" osascript -e "tell application \"${APP_NAME}\" to quit" >/dev/null 2>&1 || true

  waited=0
  while pgrep -x "$executable_name" >/dev/null 2>&1 && [[ "$waited" -lt 15 ]]; do
    sleep 1
    waited=$((waited + 1))
  done

  if pgrep -x "$executable_name" >/dev/null 2>&1; then
    pkill -x "$executable_name" || true
  fi
fi

work_dir=$(mktemp -d)
mount_point="${work_dir}/mnt"
mkdir -p "$mount_point"
mounted=0

cleanup() {
  if [[ "$mounted" -eq 1 ]]; then
    hdiutil detach "$mount_point" -quiet -force >/dev/null 2>&1 || true
  fi
  rm -rf "$work_dir"
}
trap cleanup EXIT

dmg_path="${work_dir}/${GH_ASSET}"
download_url="https://github.com/${GH_OWNER}/${GH_REPO}/releases/latest/download/${GH_ASSET}"

if ! curl -fsSL -o "$dmg_path" "$download_url"; then
  echo "Error: failed to download ${download_url}" >&2
  exit 1
fi

if ! hdiutil attach -nobrowse -quiet -mountpoint "$mount_point" "$dmg_path"; then
  echo "Error: failed to mount ${dmg_path}" >&2
  exit 1
fi
mounted=1

app_src=$(find "$mount_point" -maxdepth 1 -iname '*.app' -print -quit)
if [[ -z "$app_src" ]]; then
  echo "Error: no .app bundle found in mounted image" >&2
  exit 1
fi

rm -rf "$APP_BUNDLE"
ditto "$app_src" "$APP_BUNDLE"

if hdiutil detach "$mount_point" -quiet; then
  mounted=0
else
  hdiutil detach "$mount_point" -quiet -force || true
  mounted=0
fi

xattr -dr com.apple.quarantine "$APP_BUNDLE" 2>/dev/null || true

new_version=$(defaults read "$INFO_PLIST" CFBundleShortVersionString 2>/dev/null) || {
  echo "Error: unable to read installed version after update" >&2
  exit 1
}

if ! version_ge "$new_version" "$latest_version"; then
  echo "Error: update failed - installed version is ${new_version}, expected at least ${latest_version}" >&2
  exit 1
fi

echo "${APP_NAME} updated successfully to ${new_version}"
exit 0