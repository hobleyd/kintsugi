#!/bin/bash
set -euo pipefail

usage() {
  echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
  exit 1
}

APP_NAME=""
APP_ID=""
MODE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --appName) APP_NAME="$2"; shift 2 ;;
    --appId) APP_ID="$2"; shift 2 ;;
    --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
    --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
    *) usage ;;
  esac
done
[ -n "$APP_NAME" ] || usage
[ -n "$APP_ID" ] || usage
[ -n "$MODE" ] || usage

# The App Store's own row. The macOS agent reports the store itself as an application
# named "App Store" (see system_info::APP_STORE_NAME), and it is not an App Store app: it
# ships with macOS and is updated by softwareupdate along with the rest of the OS, which
# the agent already handles as an OS update. One script serves it and everything it
# manages so that one review covers all of them; this is where the two part ways.
is_app_store_itself() {
  [ "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')" = "app store" ]
}

latest_version() {
  local response version

  if is_app_store_itself; then
    # Declining leaves LatestVersion null, so updateAvailable is false and the agent never
    # tries this row — the same reasoning as Flatpak's own row in FlatpakUpgradeScript.
    echo "the App Store is part of macOS and updates with it; nothing to report" >&2
    return 1
  fi

  # --appId is the bundle identifier the agent read from Info.plist. Two things about this
  # URL were each learned by getting a wrong answer that looked right:
  #
  # - entity=desktopSoftware, NOT entity=macSoftware. For an app sold as one purchase on
  #   iOS and macOS (Pages, Keynote, Numbers, ...) macSoftware returns the iOS record and
  #   its version — 15.3 when the Mac build was 15.3.1 — so the row would compare against
  #   the wrong platform's release. desktopSoftware is what `mas` itself queries.
  # - No country= parameter means the US storefront. An app not sold there answers
  #   resultCount 0, the version stays null, and the row never patches — the same silent
  #   failure as any other null LatestVersion. The server cannot know a host's storefront,
  #   so this is documented rather than solved.
  #
  # The API is eventually consistent (hours to days behind the store) and rate-limited to
  # roughly twenty calls a minute; a failed check keeps the row's previous LatestVersion
  # (CheckApplicationUpdateCommandHandler), so a throttled run only delays freshness.
  response=$(curl -fsSL "https://itunes.apple.com/lookup?bundleId=${APP_ID}&entity=desktopSoftware") || return 1

  # A miss is a 200 with "resultCount":0 and an empty results array, so the count is
  # checked before anything is read out of it. Read with grep/sed because jq is not
  # something the API server is guaranteed to have.
  printf '%s' "$response" | grep -q '"resultCount":[[:space:]]*[1-9]' || return 1
  version=$(printf '%s' "$response" \
    | grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' \
    | head -1 \
    | sed -E 's/.*"([^"]*)"$/\1/')

  [ -n "$version" ] || return 1
  printf '%s' "$version"
}

if [ "$MODE" = "update-version" ]; then
  version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
  printf '%s\n' "$version"
  exit 0
fi

# --update mode: runs on the managed Mac, as root, from the agent's LaunchDaemon (see
# upgrade::runs_as_root in the macOS agent, which sends this manager's rows to the root
# queue rather than running them as the logged-in user the way Homebrew's are).
#
# Two users are needed and root can be both. Since Apple's fix for CVE-2025-43411 (macOS
# 14.8.2 / 15.7.2 / 26.1) installing a store update needs root, while starting the download
# needs the logged-in user's store session — CommerceKit talks to com.apple.appstoreagent in
# that user's gui/<uid> launchd domain, which a bare root process cannot see ("No bag
# entry"). `launchctl asuser <uid>` runs the command inside that domain while staying root;
# `mas`, seeing euid 0 and SUDO_UID, drops its effective uid to the user for the CommerceKit
# half (it refuses to run as root without SUDO_UID at all), then runs `sudo installer` for
# the install half — which asks no password because the real uid is still 0. Verified from
# a real LaunchDaemon on macOS 26.6.
if [ "$(id -u)" != 0 ]; then
  echo "this script must run as root (the agent's daemon), not as the logged-in user" >&2
  exit 1
fi

# The agent's own copy of mas, never Homebrew's: a root daemon executing a binary anyone
# else can write is root for that person. Owner and mode are checked, not just presence,
# because on an Intel Mac /usr/local/bin is Homebrew's prefix and user-owned — a swapped
# file there would be owned by whoever swapped it, which is exactly what this catches.
MAS=/usr/local/bin/kintsugi-mas
if [ ! -x "$MAS" ]; then
  echo "$MAS is not installed — it ships with the macOS agent from 0.9.0 (install.sh / self_update)" >&2
  exit 1
fi
mas_owner=$(stat -f '%u' "$MAS")
mas_mode=$(stat -f '%Lp' "$MAS")
if [ "$mas_owner" != 0 ] || [ $(( 8#$mas_mode & 8#022 )) -ne 0 ]; then
  echo "$MAS is owned by uid $mas_owner with mode $mas_mode; refusing to run anything root did not put there" >&2
  exit 1
fi

# Whoever is at the console owns the store session. The per-user process only asks for
# this row while somebody is logged in, so an empty console here is a request that outlived
# its session (see queue::REQUEST_TIMEOUT), not the normal case.
console_uid=$(stat -f '%u' /dev/console)
console_user=$(stat -f '%Su' /dev/console)
if [ -z "$console_uid" ] || [ "$console_uid" = 0 ]; then
  echo "nobody is logged in at the console, and an App Store update needs that user's store session" >&2
  exit 1
fi
console_gid=$(id -g "$console_user")
console_home=$(dscl . -read "/Users/$console_user" NFSHomeDirectory | sed -E 's/^NFSHomeDirectory: //')

# --bundle: --appId is the CFBundleIdentifier the agent reported, so mas is told so rather
# than left to guess from its shape. --verbose makes an id the store does not know a
# warning instead of silence. stdout is captured because an update that finds nothing to
# do exits 0 having printed nothing — and exit 0 is what makes the agent report the
# server's latest version as installed, so "nothing happened" has to fail here. The
# capture is outside `set -e` so a failed run's progress lines still reach the agent's log
# ahead of its exit status, rather than being dropped with the assignment.
set +e
output=$(launchctl asuser "$console_uid" /usr/bin/env \
  SUDO_UID="$console_uid" SUDO_GID="$console_gid" \
  HOME="$console_home" USER="$console_user" LOGNAME="$console_user" \
  "$MAS" update --verbose --bundle "$APP_ID")
status=$?
set -e
printf '%s\n' "$output"
[ "$status" -eq 0 ] || exit "$status"
if [ -z "$output" ]; then
  echo "the App Store reported nothing to update for $APP_ID — the installed build may already be the store's current one" >&2
  exit 1
fi