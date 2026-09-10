#!/bin/bash
set -euo pipefail

# WARNING: Based on research of https://github.com/hobleyd/recipes (GitHub API
# inspection of the "1.6.2" release), the project's release tags are bare
# version numbers (e.g. "1.6.2", no "v" prefix) and the macOS asset is named
# "recipe-manager-<version>.dmg". This naming convention is assumed stable
# going forward; if the maintainer changes the tag or asset naming scheme in a
# future release, the extraction/download logic below may need updating.

REPO_OWNER="hobleyd"
REPO_NAME="recipes"

usage() {
  echo "Usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
  exit 1
}

app_name=""
app_id=""
mode=""

while [ $# -gt 0 ]; do
  case "$1" in
    --appName)
      [ $# -ge 2 ] || usage
      app_name="$2"
      shift 2
      ;;
    --appId)
      [ $# -ge 2 ] || usage
      app_id="$2"
      shift 2
      ;;
    --update-version)
      [ -z "$mode" ] || usage
      mode="update-version"
      shift
      ;;
    --update)
      [ -z "$mode" ] || usage
      mode="update"
      shift
      ;;
    *)
      usage
      ;;
  esac
done

[ -n "$app_name" ] || usage
[ -n "$app_id" ] || usage
[ -n "$mode" ] || usage

# Compares two dotted numeric version strings. Returns success (0) if
# ver1 >= ver2, failure (1) otherwise. Avoids relying on `sort -V`, whose
# availability/behavior differs between GNU (Linux) and BSD (macOS) sort.
version_ge() {
  local ver1="$1"
  local ver2="$2"
  local -a v1
  local -a v2
  IFS=. read -r -a v1 <<< "$ver1"
  IFS=. read -r -a v2 <<< "$ver2"
  local n=${#v1[@]}
  [ "${#v2[@]}" -gt "$n" ] && n=${#v2[@]}
  local i a b
  for ((i = 0; i < n; i++)); do
    a="${v1[i]:-0}"
    b="${v2[i]:-0}"
    if ((10#$a > 10#$b)); then
      return 0
    fi
    if ((10#$a < 10#$b)); then
      return 1
    fi
  done
  return 0
}

# Determines the latest released version using only curl + text processing.
# Relies on the GitHub "/releases/latest" redirect, whose target URL ends in
# the release tag - no JSON parsing required. Prints the bare version to
# stdout on success; prints nothing and returns non-zero on failure.
latest_version() {
  local redirect tag
  redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' \
    "https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/latest") || return 1
  [ -n "$redirect" ] || return 1
  tag="${redirect##*/}"
  tag="${tag#v}"
  tag="${tag#recipe-manager-}"
  [ -n "$tag" ] || return 1
  printf '%s' "$tag"
}

if [ "$mode" = "update-version" ]; then
  version=""
  if ! version="$(latest_version)"; then
    echo "Error: unable to determine latest version of ${app_name} from GitHub" >&2
    exit 1
  fi
  printf '%s\n' "$version"
  exit 0
fi

# --update mode: runs on the managed Mac itself, as root, outside any GUI session.

latest=""
if ! latest="$(latest_version)"; then
  echo "Error: unable to determine latest version of ${app_name} from GitHub" >&2
  exit 1
fi

app_bundle="/Applications/${app_name}.app"
info_plist="${app_bundle}/Contents/Info.plist"

if [ ! -f "$info_plist" ]; then
  echo "Error: ${info_plist} not found; is ${app_name} installed?" >&2
  exit 1
fi

installed_id="$(defaults read "$info_plist" CFBundleIdentifier)"
if [ "$installed_id" != "$app_id" ]; then
  echo "Error: installed bundle identifier '${installed_id}' does not match expected '${app_id}' (wrong app)" >&2
  exit 1
fi

installed_version="$(defaults read "$info_plist" CFBundleShortVersionString)"

if version_ge "$installed_version" "$latest"; then
  echo "${app_name} is already up to date (installed ${installed_version}, latest ${latest})"
  exit 0
fi

echo "Updating ${app_name} from ${installed_version} to ${latest}..."

# Quit the app gracefully if it is currently running, from the console user's
# session (this script runs as root outside any GUI session so it cannot
# reach the app directly).
exec_name="$(defaults read "$info_plist" CFBundleExecutable 2>/dev/null || true)"
if [ -z "$exec_name" ]; then
  exec_name="$app_name"
fi

if pgrep -x "$exec_name" >/dev/null 2>&1; then
  console_uid="$(stat -f %u /dev/console)"
  launchctl asuser "$console_uid" osascript -e "tell application \"${app_name}\" to quit" || true

  waited=0
  while pgrep -x "$exec_name" >/dev/null 2>&1 && [ "$waited" -lt 15 ]; do
    sleep 1
    waited=$((waited + 1))
  done

  if pgrep -x "$exec_name" >/dev/null 2>&1; then
    pkill -x "$exec_name" || true
  fi
fi

tmpdir="$(mktemp -d)"
cleanup() {
  if [ -n "${mountpoint:-}" ] && mount | grep -q "on ${mountpoint} "; then
    hdiutil detach -quiet "$mountpoint" >/dev/null 2>&1 || true
  fi
  rm -rf "$tmpdir"
}
trap cleanup EXIT

asset_name="recipe-manager-${latest}.dmg"
download_url="https://github.com/${REPO_OWNER}/${REPO_NAME}/releases/latest/download/${asset_name}"
dmg_path="${tmpdir}/${asset_name}"

if ! curl -fsSL -o "$dmg_path" "$download_url"; then
  echo "Error: failed to download ${download_url}" >&2
  exit 1
fi

mountpoint="${tmpdir}/mnt"
mkdir -p "$mountpoint"

if ! hdiutil attach -nobrowse -quiet -mountpoint "$mountpoint" "$dmg_path"; then
  echo "Error: failed to mount ${dmg_path}" >&2
  exit 1
fi

new_app_bundle="$(find "$mountpoint" -maxdepth 1 -name '*.app' -print -quit)"
if [ -z "$new_app_bundle" ]; then
  echo "Error: no .app bundle found in downloaded disk image" >&2
  exit 1
fi

rm -rf "$app_bundle"
ditto "$new_app_bundle" "$app_bundle"

hdiutil detach -quiet "$mountpoint" >/dev/null 2>&1 || true
mountpoint=""

xattr -dr com.apple.quarantine "$app_bundle" || true

new_installed_version="$(defaults read "$info_plist" CFBundleShortVersionString)"
if ! version_ge "$new_installed_version" "$latest"; then
  echo "Error: update failed; installed version is ${new_installed_version}, expected at least ${latest}" >&2
  exit 1
fi

echo "${app_name} successfully updated to ${new_installed_version}"
exit 0