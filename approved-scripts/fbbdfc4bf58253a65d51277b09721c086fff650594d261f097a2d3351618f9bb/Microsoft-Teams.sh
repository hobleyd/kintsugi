#!/bin/bash
set -euo pipefail

# WARNING: Microsoft Teams (new Teams, com.microsoft.teams2) has no public update
# API, RSS/Atom feed, or GitHub/GitLab release catalog to query for "latest version".
# The most reliable text-scrapable official source found during research is
# Microsoft's own documentation page listing Teams client version history
# (https://learn.microsoft.com/en-us/officeupdates/teams-app-versioning), which
# contains a "Mac" table whose first (topmost) data row is the newest release,
# e.g. "| 2026 | September 18 (rolling out) | 26225.1708.5124.9749 | ... |".
# This script locates that table by finding the standalone heading line "Mac"
# after stripping HTML tags, then takes the first 4-part dotted numeric token
# (Teams version column) that follows it, before the next section heading
# ("Mobile"). This is heuristic HTML scraping of a docs page, not a stable API:
# if Microsoft restructures that page (renames the heading, reorders sections,
# changes the version format) this will start failing loudly (see error
# handling below) rather than silently returning a wrong version.
# For --update, the download uses Microsoft's official "evergreen" fwlink
# redirector (https://go.microsoft.com/fwlink/?linkid=2249065) which, verified
# live during this research pass (2026-09-24), 302-redirects to
# https://statics.teams.cdn.office.net/production-osx/enterprise/webview2/lkg/MicrosoftTeams.pkg
# — a stable "latest" CDN path with no version number embedded, analogous to a
# GitHub "releases/latest/download" URL. If Microsoft ever retires this fwlink
# ID, it would need to be replaced with whatever new one Microsoft publishes on
# https://www.microsoft.com/en-us/microsoft-teams/download-app.
# I did not have the ability to execute curl against these live URLs from this
# sandboxed session to test the parsing pipeline end-to-end; the redirect chain
# was confirmed via a fetch, but the exact byte-for-byte HTML shape of the
# versioning table (markdown pipe-table vs. rendered <table>) was not directly
# inspected, so the tag-stripping approach below is intentionally defensive
# (works whether the table renders as HTML <table> or literal markdown pipes).
#
# --update-version must work on a plain Linux server with nothing but curl and
# standard coreutils available (no macOS-only tools). fetch_latest_version()
# below is the only code path exercised by --update-version, and it uses only
# curl/sed/grep/cut/head/tail/printf. All macOS-only commands (defaults,
# installer, launchctl, osascript, pgrep, pkill, stat -f) are confined to the
# --update code path, which runs on the managed Mac itself.

VERSIONING_URL="https://learn.microsoft.com/en-us/officeupdates/teams-app-versioning"
PKG_URL="https://go.microsoft.com/fwlink/?linkid=2249065"

usage() {
  echo "Usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
  exit 1
}

appName=""
appId=""
mode=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --appName)
      [[ $# -ge 2 ]] || usage
      appName="$2"
      shift 2
      ;;
    --appId)
      [[ $# -ge 2 ]] || usage
      appId="$2"
      shift 2
      ;;
    --update-version)
      [[ -z "$mode" ]] || usage
      mode="version"
      shift
      ;;
    --update)
      [[ -z "$mode" ]] || usage
      mode="update"
      shift
      ;;
    *)
      usage
      ;;
  esac
done

[[ -n "$appName" && -n "$appId" && -n "$mode" ]] || usage

# Compares two dotted numeric versions. Returns success (0) if $1 >= $2.
version_ge() {
  local -a v1 v2
  IFS=. read -ra v1 <<< "$1"
  IFS=. read -ra v2 <<< "$2"
  local n=${#v1[@]}
  local m=${#v2[@]}
  local max=$(( n > m ? n : m ))
  local i a b
  for (( i = 0; i < max; i++ )); do
    a=${v1[i]:-0}
    b=${v2[i]:-0}
    a=${a//[!0-9]/}
    b=${b//[!0-9]/}
    a=${a:-0}
    b=${b:-0}
    if (( 10#$a > 10#$b )); then
      return 0
    fi
    if (( 10#$a < 10#$b )); then
      return 1
    fi
  done
  return 0
}

# Fetches the Teams "Mac" version-history table and prints the newest version
# string to stdout. Uses only curl + grep/sed. No filesystem writes.
fetch_latest_version() {
  local html
  html="$(curl -fsS --max-time 30 "$VERSIONING_URL" 2>/dev/null)" || {
    echo "Error: failed to fetch $VERSIONING_URL" >&2
    return 1
  }

  local text
  text=$(printf '%s' "$html" \
    | sed -e 's/</\n</g' -e 's/>/>\n/g' \
    | sed -e 's/<[^>]*>//g' \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
    | sed -e '/^$/d')

  local mac_line
  mac_line=$(printf '%s\n' "$text" | grep -n -x -m1 'Mac' | cut -d: -f1 || true)
  if [[ -z "$mac_line" ]]; then
    echo "Error: could not locate 'Mac' section heading in $VERSIONING_URL" >&2
    return 1
  fi

  local rest
  rest=$(printf '%s\n' "$text" | tail -n +"$(( mac_line + 1 ))")

  local end_line
  end_line=$(printf '%s\n' "$rest" \
    | grep -n -x -m1 -E 'Mobile|Web|Windows|VDI|Virtual desktop infrastructure \(VDI\)' \
    | cut -d: -f1 || true)

  local section
  if [[ -n "$end_line" ]]; then
    section=$(printf '%s\n' "$rest" | head -n "$(( end_line - 1 ))")
  else
    section=$(printf '%s\n' "$rest" | head -n 400)
  fi

  local version
  version=$(printf '%s\n' "$section" | grep -oE '[0-9]{4,6}(\.[0-9]+){3}' | head -n1 || true)
  if [[ -z "$version" ]]; then
    echo "Error: could not parse a Teams version number out of the Mac section of $VERSIONING_URL" >&2
    return 1
  fi

  printf '%s\n' "$version"
}

if [[ "$mode" == "version" ]]; then
  latest="$(fetch_latest_version)"
  printf '%s\n' "$latest"
  exit 0
fi

# --update mode: runs on the managed Mac itself, as root, outside any GUI session.

latest="$(fetch_latest_version)"

appDir="/Applications/${appName}.app"
plist="${appDir}/Contents/Info.plist"

if [[ ! -f "$plist" ]]; then
  echo "Error: ${plist} not found; cannot locate installed ${appName}" >&2
  exit 1
fi

installed_id="$(defaults read "$plist" CFBundleIdentifier 2>/dev/null)" || {
  echo "Error: failed to read CFBundleIdentifier from ${plist}" >&2
  exit 1
}

if [[ "$installed_id" != "$appId" ]]; then
  echo "Error: installed app at ${appDir} has bundle id '${installed_id}', expected '${appId}' - refusing to touch it" >&2
  exit 1
fi

installed_version="$(defaults read "$plist" CFBundleShortVersionString 2>/dev/null)" || {
  echo "Error: failed to read CFBundleShortVersionString from ${plist}" >&2
  exit 1
}

if version_ge "$installed_version" "$latest"; then
  echo "${appName} is already up to date (installed ${installed_version}, latest ${latest})"
  exit 0
fi

echo "Updating ${appName} from ${installed_version} to ${latest}"

if pgrep -x "$appName" >/dev/null 2>&1; then
  echo "Quitting running ${appName} instance..."
  console_uid="$(stat -f %u /dev/console)"
  launchctl asuser "$console_uid" osascript -e "tell application \"${appName}\" to quit" || true

  waited=0
  while pgrep -x "$appName" >/dev/null 2>&1 && [[ $waited -lt 15 ]]; do
    sleep 1
    waited=$(( waited + 1 ))
  done

  if pgrep -x "$appName" >/dev/null 2>&1; then
    echo "Warning: ${appName} still running after grace period, force-killing" >&2
    pkill -x "$appName" || true
    sleep 2
  fi
fi

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT

pkg_path="${tmpdir}/MicrosoftTeams.pkg"

if ! curl -fsSL --max-time 300 -o "$pkg_path" "$PKG_URL"; then
  echo "Error: failed to download ${appName} installer from ${PKG_URL}" >&2
  exit 1
fi

if ! installer -pkg "$pkg_path" -target /; then
  echo "Error: installer failed for ${pkg_path}" >&2
  exit 1
fi

new_version="$(defaults read "$plist" CFBundleShortVersionString 2>/dev/null)" || {
  echo "Error: failed to read installed version after update" >&2
  exit 1
}

if ! version_ge "$new_version" "$latest"; then
  echo "Error: after update, installed version (${new_version}) does not meet latest known version (${latest})" >&2
  exit 1
fi

echo "Successfully updated ${appName} to version ${new_version}"
exit 0