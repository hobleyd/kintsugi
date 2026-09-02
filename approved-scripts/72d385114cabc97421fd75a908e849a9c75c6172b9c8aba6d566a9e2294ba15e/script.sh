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

latest_version() {
  local response
  response=$(curl -fsSL "https://formulae.brew.sh/api/formula/${APP_NAME}.json" 2>/dev/null) || \
    response=$(curl -fsSL "https://formulae.brew.sh/api/cask/${APP_NAME}.json" 2>/dev/null) || return 1

  local version
  version=$(printf '%s' "$response" | grep -o '"stable":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
  if [ -z "$version" ]; then
    version=$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
  fi

  [ -n "$version" ] || return 1
  printf '%s' "$version"
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