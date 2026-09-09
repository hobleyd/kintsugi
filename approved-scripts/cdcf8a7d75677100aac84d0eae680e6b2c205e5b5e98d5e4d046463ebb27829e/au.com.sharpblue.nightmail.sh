#!/bin/bash
set -euo pipefail

# WARNING: The bundle id au.com.sharpblue.nightmail was cross-referenced against
# "Sharp Blue" (David Hobley's business, sharpblue.com.au) and the GitHub user
# "hobleyd" (Hobley, D.) to identify github.com/hobleyd/nightmail as the correct
# upstream project (a Flutter-based Office365/Google Workspace/IMAP email client),
# out of several unrelated same-named repos/sites turned up by search. At research
# time its latest release was 1.23.1 with a stable, unversioned "nightmail.dmg"
# asset attached to every release (unlike the versioned .apk/.exe assets), which
# is what makes the releases/latest/download/nightmail.dmg URL used below work
# without needing to know the version in advance. This could not be tested against
# a live macOS host or a real install of the app, so the --update code path
# (mount/copy/quarantine-strip/quit sequence) is based on the documented contract
# and standard macOS behavior rather than an end-to-end run. It also assumes the
# running process name and the top-level .app inside the dmg match --appName;
# if a future release renames the dmg asset or ships multiple .app bundles in the
# image, this script's assumptions would need revisiting.

OWNER="hobleyd"
REPO="nightmail"
DMG_ASSET="nightmail.dmg"

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

# Compares two dotted-numeric version strings without relying on GNU sort -V
# (not available in macOS's BSD sort). Returns 0 (true) if $1 >= $2.
version_ge() {
  local -a v1 v2
  IFS=. read -ra v1 <<< "$1"
  IFS=. read -ra v2 <<< "$2"
  local max=${#v1[@]}
  if (( ${#v2[@]} > max )); then max=${#v2[@]}; fi
  local i a b
  for ((i = 0; i < max; i++)); do
    a=${v1[i]:-0}
    b=${v2[i]:-0}
    if ((10#$a > 10#$b)); then return 0; fi
    if ((10#$a < 10#$b)); then return 1; fi
  done
  return 0
}

# Uses the redirect target of the GitHub "latest release" URL to discover the
# current version without parsing JSON. Prints the bare version to stdout.
get_latest_version() {
  local redirect version
  redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' \
    "https://github.com/${OWNER}/${REPO}/releases/latest") || {
    echo "Error: failed to query https://github.com/${OWNER}/${REPO}/releases/latest" >&2
    return 1
  }
  if [[ -z "$redirect" ]]; then
    echo "Error: no redirect returned for ${OWNER}/${REPO} releases/latest" >&2
    return 1
  fi
  version="${redirect##*/}"
  version="${version#v}"
  if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+)*$ ]]; then
    echo "Error: could not parse a version from redirect target: $redirect" >&2
    return 1
  fi
  printf '%s\n' "$version"
}

if [[ "$MODE" == "update-version" ]]; then
  latest_version=$(get_latest_version) || exit 1
  printf '%s\n' "$latest_version"
  exit 0
fi

# --update mode below: runs on the managed Mac itself, as root, outside any GUI session.

APP_PATH="/Applications/${APP_NAME}.app"
PLIST="$APP_PATH/Contents/Info.plist"

if [[ ! -f "$PLIST" ]]; then
  echo "Error: $PLIST not found; cannot verify installed application" >&2
  exit 1
fi

installed_id=$(defaults read "$PLIST" CFBundleIdentifier)
if [[ "$installed_id" != "$APP_ID" ]]; then
  echo "Error: installed bundle identifier '$installed_id' at $APP_PATH does not match expected '$APP_ID'" >&2
  exit 1
fi

installed_version=$(defaults read "$PLIST" CFBundleShortVersionString)

latest_version=$(get_latest_version) || exit 1

if version_ge "$installed_version" "$latest_version"; then
  echo "$APP_NAME is already up to date (installed $installed_version, latest $latest_version)"
  exit 0
fi

echo "Updating $APP_NAME from $installed_version to $latest_version"

if pgrep -x "$APP_NAME" >/dev/null 2>&1; then
  console_uid=$(stat -f %u /dev/console)
  launchctl asuser "$console_uid" osascript -e "tell application \"$APP_NAME\" to quit" >/dev/null 2>&1 || true
  for _ in $(seq 1 15); do
    pgrep -x "$APP_NAME" >/dev/null 2>&1 || break
    sleep 1
  done
  if pgrep -x "$APP_NAME" >/dev/null 2>&1; then
    pkill -x "$APP_NAME" || true
  fi
fi

tmpdir=$(mktemp -d)
mountpoint="$tmpdir/mnt"
cleanup() {
  hdiutil detach "$mountpoint" -quiet >/dev/null 2>&1 || true
  rm -rf "$tmpdir"
}
trap cleanup EXIT

dmg_path="$tmpdir/${DMG_ASSET}"
curl -fsSL -o "$dmg_path" \
  "https://github.com/${OWNER}/${REPO}/releases/latest/download/${DMG_ASSET}"

mkdir -p "$mountpoint"
hdiutil attach -nobrowse -quiet -mountpoint "$mountpoint" "$dmg_path"

src_app=$(find "$mountpoint" -maxdepth 1 -iname "*.app" -print -quit)
if [[ -z "$src_app" ]]; then
  echo "Error: no .app bundle found in downloaded disk image" >&2
  exit 1
fi

rm -rf "$APP_PATH"
cp -R "$src_app" "$APP_PATH"

hdiutil detach "$mountpoint" -quiet >/dev/null 2>&1 || true

xattr -dr com.apple.quarantine "$APP_PATH" || true

new_version=$(defaults read "$PLIST" CFBundleShortVersionString)
if ! version_ge "$new_version" "$latest_version"; then
  echo "Error: update verification failed; installed version is $new_version, expected at least $latest_version" >&2
  exit 1
fi

echo "Successfully updated $APP_NAME to $new_version"