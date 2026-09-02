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

# Both URLs are case-sensitive, and $APP_NAME is not always the lowercase token brew
# knows the package by: PrepareUpgradePathScanQueryHandler groups an application's
# variants case-insensitively, so a row whose name settled on a display-cased
# /Applications bundle ("Nextcloud") rather than on `brew list`'s output ("nextcloud")
# 404s on every URL below and leaves LatestVersion null — which makes updateAvailable
# false, which makes the agent's is_patchable false, so the application silently never
# patches. `brew` itself downcases its argument, so only this lookup is affected.
# Tried as given first: a row already named by its token must not be transformed.
latest_version() {
  local candidate response version
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

brew update && brew upgrade "$APP_NAME"