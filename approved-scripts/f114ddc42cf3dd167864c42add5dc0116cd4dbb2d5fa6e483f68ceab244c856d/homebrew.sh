#!/bin/bash
set -euo pipefail

usage() {
  echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
  exit 1
}

APP_NAME=""
MODE=""
while [ $# -gt 0 ]; do
  case "$1" in
    --appName) APP_NAME="$2"; shift 2 ;;
    --appId) shift 2 ;;
    --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
    --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
    *) usage ;;
  esac
done
[ -n "$APP_NAME" ] || usage
[ -n "$MODE" ] || usage

# Homebrew's own row. The agent reports Homebrew itself as an application named "Homebrew"
# (see system_info::HOMEBREW_NAME in the macOS agent), and it is not a formula: neither
# URL below knows it, and `brew upgrade homebrew` would be an error. One script serves it
# and everything it manages so that one review covers all of them; this is where the two
# part ways.
is_homebrew_itself() {
  [ "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')" = "homebrew" ]
}

# Both URLs are case-sensitive, and $APP_NAME is not always the lowercase token brew
# knows the package by: PrepareUpgradePathScanQueryHandler groups an application's
# variants case-insensitively, so a row whose name settled on a display-cased
# /Applications bundle ("Nextcloud") rather than on `brew list`'s output ("nextcloud")
# 404s on every URL below and leaves LatestVersion null — which makes updateAvailable
# false, which makes the agent's is_patchable false, so the application silently never
# patches. `brew` itself downcases its argument, so only this lookup is affected.
# Tried as given first: a row already named by its token must not be transformed.
latest_version() {
  local candidate response version redirect

  if is_homebrew_itself; then
    # -fsS, deliberately NOT -fsSL: %{redirect_url} reports the redirect curl did *not*
    # follow, so adding -L makes curl follow it to the tag page and report an empty
    # string — the check then fails, LatestVersion stays null, and Homebrew's own row
    # silently never updates.
    redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/Homebrew/brew/releases/latest") || return 1
    [ -n "$redirect" ] || return 1
    printf '%s' "${redirect##*/}"
    return 0
  fi

  for candidate in "$APP_NAME" "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')"; do
    response=$(curl -fsSL "https://formulae.brew.sh/api/formula/${candidate}.json" 2>/dev/null) || \
      response=$(curl -fsSL "https://formulae.brew.sh/api/cask/${candidate}.json" 2>/dev/null) || continue

    version=$(printf '%s' "$response" | grep -o '"stable":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
    if [ -z "$version" ]; then
      version=$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
    fi

    [ -n "$version" ] || continue
    printf '%s' "$version"
    return 0
  done

  return 1
}

if [ "$MODE" = "update-version" ]; then
  version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
  printf '%s\n' "$version"
  exit 0
fi

# --update mode: runs on the managed Mac itself, where `brew` actually exists.
if ! command -v brew >/dev/null 2>&1; then
  echo "homebrew is not installed" >&2
  exit 1
fi

# `brew update` is Homebrew upgrading itself — it pulls brew's own repository to its latest
# release tag — and is also what refreshes the formula/cask index; without it `brew upgrade`
# acts on a stale catalog and reports "already up to date" for a version released since the
# index was last refreshed. So every application upgrade brings Homebrew along, and for
# Homebrew's own row it is the whole upgrade: the applications each have a row of their own.
brew update

if is_homebrew_itself; then
  exit 0
fi

brew upgrade "$APP_NAME"