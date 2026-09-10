#!/bin/bash
set -euo pipefail

# WARNING: Research was limited to GitHub's web/API views (no local curl testing was
# possible in the research environment). Confirmed via https://api.github.com/repos/
# hobleyd/shackleton/releases/latest that the 1.14.0 macOS asset is named literally
# "shackleton.dmg" (not version-stamped), so the releases/latest/download/ stable URL
# is used. If a future release renames this asset, the download step below will fail
# with a clear curl error rather than silently doing the wrong thing.

GH_OWNER="hobleyd"
GH_REPO="shackleton"
GH_REPO_URL="https://github.com/${GH_OWNER}/${GH_REPO}"
DMG_ASSET_NAME="shackleton.dmg"

usage() {
  echo "Usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
}

app_name=""
app_id=""
mode=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --appName)
      [ "$#" -ge 2 ] || { usage; exit 1; }
      app_name="$2"
      shift 2
      ;;
    --appId)
      [ "$#" -ge 2 ] || { usage; exit 1; }
      app_id="$2"
      shift 2
      ;;
    --update-version)
      [ -z "$mode" ] || { usage; exit 1; }
      mode="update-version"
      shift
      ;;
    --update)
      [ -z "$mode" ] || { usage; exit 1; }
      mode="update"
      shift
      ;;
    *)
      usage
      exit 1
      ;;
  esac
done

if [ -z "$app_name" ] || [ -z "$app_id" ] || [ -z "$mode" ]; then
  usage
  exit 1
fi

# Prints the latest stable version (bare, e.g. "1.14.0") on stdout, nothing else.
# Returns non-zero and prints nothing to stdout on failure.
get_latest_version() {
  local redirect version

  redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' "${GH_REPO_URL}/releases/latest" 2>/dev/null) || {
    echo "error: failed to query ${GH_REPO_URL}/releases/latest" >&2
    return 1
  }

  if [ -z "$redirect" ]; then
    echo "error: no redirect returned by ${GH_REPO_URL}/releases/latest (unexpected response)" >&2
    return 1
  fi

  version="${redirect##*/}"

  if ! echo "$version" | grep -Eq '^[0-9]+(\.[0-9]+){1,3}$'; then
    echo "error: unexpected latest-version string parsed from redirect: '${redirect}'" >&2
    return 1
  fi

  echo "$version"
}

# Returns success (0) if version $1 >= version $2, using sort -V.
version_ge() {
  local highest
  [ "$1" = "$2" ] && return 0
  highest=$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n1)
  [ "$highest" = "$1" ]
}

if [ "$mode" = "update-version" ]; then
  latest_version=$(get_latest_version) || exit 1
  echo "$latest_version"
  exit 0
fi

# --update mode: runs on the managed Mac itself, as root, outside any GUI session.

latest_version=$(get_latest_version) || exit 1

app_bundle="/Applications/${app_name}.app"
info_plist="${app_bundle}/Contents/Info.plist"

if [ ! -d "$app_bundle" ]; then
  echo "error: ${app_bundle} not found; cannot update an application that is not installed" >&2
  exit 1
fi

installed_version=$(defaults read "$info_plist" CFBundleShortVersionString 2>/dev/null) || {
  echo "error: failed to read CFBundleShortVersionString from ${info_plist}" >&2
  exit 1
}

installed_id=$(defaults read "$info_plist" CFBundleIdentifier 2>/dev/null) || {
  echo "error: failed to read CFBundleIdentifier from ${info_plist}" >&2
  exit 1
}

if [ "$installed_id" != "$app_id" ]; then
  echo "error: installed bundle identifier '${installed_id}' does not match expected '${app_id}' (wrong app at ${app_bundle}); refusing to touch it" >&2
  exit 1
fi

if version_ge "$installed_version" "$latest_version"; then
  echo "${app_name} is already up to date (installed ${installed_version}, latest ${latest_version})"
  exit 0
fi

echo "${app_name}: updating ${installed_version} -> ${latest_version}"

if pgrep -x "$app_name" >/dev/null 2>&1; then
  echo "${app_name} is running; requesting graceful quit"
  console_uid="$(stat -f %u /dev/console)"
  launchctl asuser "$console_uid" osascript -e "tell application \"${app_name}\" to quit" >/dev/null 2>&1 || true

  waited=0
  while [ "$waited" -lt 15 ] && pgrep -x "$app_name" >/dev/null 2>&1; do
    sleep 1
    waited=$((waited + 1))
  done

  if pgrep -x "$app_name" >/dev/null 2>&1; then
    echo "${app_name} did not quit gracefully after ${waited}s; forcing termination" >&2
    pkill -x "$app_name" || true
    sleep 1
  fi
fi

tmp_dir="$(mktemp -d)"
mount_point="${tmp_dir}/mnt"
mkdir -p "$mount_point"

cleanup() {
  if hdiutil info 2>/dev/null | grep -q "$mount_point"; then
    hdiutil detach -quiet "$mount_point" >/dev/null 2>&1 || true
  fi
  rm -rf "$tmp_dir"
}
trap cleanup EXIT

dmg_path="${tmp_dir}/${DMG_ASSET_NAME}"

if ! curl -fsSL "${GH_REPO_URL}/releases/latest/download/${DMG_ASSET_NAME}" -o "$dmg_path"; then
  echo "error: failed to download ${GH_REPO_URL}/releases/latest/download/${DMG_ASSET_NAME}" >&2
  exit 1
fi

if ! hdiutil attach -nobrowse -quiet -mountpoint "$mount_point" "$dmg_path"; then
  echo "error: failed to mount downloaded disk image ${dmg_path}" >&2
  exit 1
fi

app_src="$(find "$mount_point" -maxdepth 1 -iname '*.app' -print -quit)"

if [ -z "$app_src" ]; then
  echo "error: no .app bundle found inside mounted disk image" >&2
  exit 1
fi

rm -rf "$app_bundle"
cp -R "$app_src" "$app_bundle"

hdiutil detach -quiet "$mount_point" >/dev/null 2>&1 || true

xattr -dr com.apple.quarantine "$app_bundle" 2>/dev/null || true

new_version=$(defaults read "$info_plist" CFBundleShortVersionString 2>/dev/null) || {
  echo "error: failed to read installed version after update" >&2
  exit 1
}

if ! version_ge "$new_version" "$latest_version"; then
  echo "error: update did not succeed; installed version is now ${new_version}, expected at least ${latest_version}" >&2
  exit 1
fi

echo "${app_name} updated successfully to ${new_version}"
exit 0