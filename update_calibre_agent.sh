#!/bin/bash
# update_calibre_agent.sh
#
# Fleet update tool for "Calibre Agent" (bundle id au.com.sharpblue.calibreagent).
#
# Calibre Agent is an open-source Flutter desktop app (macOS/Linux/Windows) published
# by Sharpblue, hosted at https://github.com/hobleyd/calibre_agent and distributed
# through GitHub Releases. Each release ships a macOS asset named "calibre-agent.dmg"
# (a Developer-ID build, NOT a Mac App Store app), plus .exe/.flatpak/.zip for other
# platforms. The repo uses plain semver tags with NO leading "v" (e.g. 1.0.2).
#
# Latest version is discovered at runtime via the GitHub "releases/latest" HTTP 302
# redirect, so no version number is baked in and the tool keeps working for releases
# that do not exist yet.
#
# CLI:
#   update_calibre_agent.sh --appName <name> --appId <bundle-id> --update-version
#   update_calibre_agent.sh --appName <name> --appId <bundle-id> --update
#
# --update-version : pure HTTP check (runs on a plain Linux server, no macOS tools,
#                   no filesystem access). Prints the bare latest version to stdout.
# --update          : runs on the managed Mac. Checks latest, verifies the installed
#                   bundle's identifier, gracefully quits if running, downloads the
#                   current .dmg from a stable "latest" URL, replaces /Applications,
#                   strips the quarantine xattr, and verifies the resulting version.
#
# WARNING: Live web access was used to confirm https://github.com/hobleyd/calibre_agent
#          as the vendor repository for au.com.sharpblue.calibreagent (latest tag 1.0.2,
#          matching the installed 1.0.2, macOS asset "calibre-agent.dmg", and the bundle
#          identifier au.com.sharpblue.calibreagent read back from a freshly downloaded copy
#          of the shipped .dmg). The macOS-only --update path was NOT run end-to-end on a live
#          managed Mac during authoring, so the identifier check, the graceful-quit sequence,
#          the hdiutil mount/stage/detach/copy, the xattr strip, and the final version
#          verification are unverified at runtime and rely on standard behaviour. The graceful
#          quit uses "tell application id <appId> to quit" -- the reliable way to stop this
#          menu-bar/agent app, which registers no human-readable AppleEvent application name;
#          the process is then polled and, as a last resort only, killed via pgrep/pkill using
#          --appName (which for Calibre Agent equals its CFBundleExecutable "Calibre Agent").
#          Non-.dmg forms (.pkg/.zip) are handled generically by asset name, but the .dmg is
#          the only form actually shipped (verified today).
#
# WARNING: The macOS --update path depends on defaults/hdiutil/xattr/osascript/installer/ditto
#          which exist only on macOS; --update-version is fully OS-independent and uses only
#          curl + sed/grep.

set -euo pipefail

# ---- Vendor distribution source (discovered via live research) --------------------
REPO="hobleyd/calibre_agent"
# Stable, version-less macOS asset name across all known releases.
DMG_ASSET="calibre-agent.dmg"
PKG_ASSET="calibre-agent.pkg"
ZIP_ASSET="calibre-agent.zip"
# The .dmg is the only macOS form that is actually published; the .pkg/.zip names are probed
# for robustness if the layout ever changes, but a per-version URL is intentionally NOT
# constructed because the stable version-less "latest/download/<asset>" pattern already
# resolves to whatever is current.

usage() {
  printf 'usage: %s --appName <name> --appId <bundle-id> (--update-version | --update)\n' \
    "${0##*/}" >&2
  exit 2
}

die_usage() { usage; }

# ---- Argument parsing (order-independent; exactly one of the two modes) ----------
APP_NAME=""
APP_ID=""
MODE=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --appName)
      [ "$#" -ge 2 ] || die_usage
      [ -n "$2" ] || die_usage
      APP_NAME="$2"; shift 2 ;;
    --appId)
      [ "$#" -ge 2 ] || die_usage
      [ -n "$2" ] || die_usage
      APP_ID="$2"; shift 2 ;;
    --update-version)
      [ -z "$MODE" ] || die_usage
      MODE="update-version"; shift ;;
    --update)
      [ -z "$MODE" ] || die_usage
      MODE="update"; shift ;;
    *=*)
      die_usage ;;
    *)
      die_usage ;;
  esac
done

# All three pieces required: --appName, --appId, and exactly one mode.
if [ -z "$APP_NAME" ] || [ -z "$APP_ID" ] || [ -z "$MODE" ]; then
  die_usage
fi

APP_TARGET="/Applications/${APP_NAME}.app"

# ---- Shared: discover the latest released version (pure HTTP + text, no jq) -------
# Uses the GitHub "releases/latest" HTTP 302 redirect. IMPORTANT: no -L, because
# '%{redirect_url}' reports the redirect curl did NOT follow; adding -L makes curl
# follow it and curl then reports an empty redirect_url.
fetch_latest_version() {
  local url redirect tag
  url="https://github.com/${REPO}/releases/latest"
  if ! redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' --max-time 30 "$url"); then
    echo "error: could not reach GitHub to determine latest version for ${REPO}" >&2
    return 1
  fi
  # The redirect always ends in ".../releases/tag/<TAG>". Anchor on that suffix (the host
  # may be github.com with owner and repo as separate path segments in front of it), drop
  # an optional leading "v" and any trailing slash, and keep the tag only.
  tag=$(printf '%s' "$redirect" | sed -E 's#.*/releases/tag/##; s#^v##; s#/$##')
  # A failed parse leaves an un-stripped URL, an empty string, or a non-numeric string.
  if [ -z "$tag" ] || printf '%s' "$tag" | grep -q 'http://\|https://' || ! printf '%s' "$tag" | grep -q '[0-9]'; then
    echo "error: could not parse a version from GitHub response for ${REPO}" >&2
    return 1
  fi
  printf '%s' "$tag"
}

# ---- Version comparison (portable; no `sort -V` for BSD/macOS) ------------------
# Returns 0 if $1 >= $2, non-zero otherwise. Strips a leading 'v' and any build-metadata
# after a '+' (e.g. "1.0.0+1" -> "1.0.0"), then compares dotted numeric components.
ver_ge() {
  local a b
  a=${1#v}; b=${2#v}
  a=${a%%+*}; b=${b%%+*}
  local -a A=() B=()
  IFS='.' read -r -a A <<<"$a" || true
  IFS='.' read -r -a B <<<"$b" || true
  local n=${#A[@]}
  if [ "${#B[@]}" -gt "$n" ]; then n=${#B[@]}; fi
  local i aa bb
  for ((i = 0; i < n; i++)); do
    aa=${A[i]:-0}; bb=${B[i]:-0}
    aa=${aa%%[!0-9]*}; bb=${bb%%[!0-9]*}
    aa=${aa:-0}; bb=${bb:-0}
    if ((10#$aa > 10#$bb)); then return 0; fi
    if ((10#$aa < 10#$bb)); then return 1; fi
  done
  return 0
}

# ---- macOS helpers ---------------------------------------------------------------
# Reads the installed bundle's short version (or CFBundleVersion as a fallback).
read_installed_version() {
  local short full
  short=$(defaults read "${APP_TARGET}/Contents/Info.plist" CFBundleShortVersionString 2>/dev/null || true)
  if [ -z "$short" ]; then
    full=$(defaults read "${APP_TARGET}/Contents/Info.plist" CFBundleVersion 2>/dev/null || true)
    if [ -n "$full" ]; then printf '%s' "$full"; fi
  else
    printf '%s' "$short"
  fi
}

# Reads the installed bundle's identifier (for the "did we open the right app" check).
read_installed_id() {
  defaults read "${APP_TARGET}/Contents/Info.plist" CFBundleIdentifier 2>/dev/null || true
}

# Gracefully quit the running app; bounded polling; pkill only as a last resort.
quit_app_gracefully() {
  local waited=0
  # This app is a menu-bar/agent app that does not register a human-readable
  # AppleEvent application name, so quit it by CFBundleIdentifier, which is reliable.
  osascript -e "tell application id \"${APP_ID}\" to quit" >/dev/null 2>&1 || true
  while [ "$waited" -lt 15 ]; do
    if ! pgrep -x "$APP_NAME" >/dev/null 2>&1; then
      return 0
    fi
    sleep 1
    waited=$((waited + 1))
  done
  # Still alive after the grace period: last-resort hard kill.
  pkill -x "$APP_NAME" 2>/dev/null || true
  sleep 1
  return 0
}

# Resolve which macOS release asset is current and record its stable "latest" URL and kind
# into the globals URL and ASSET_KIND (declared in the main flow). Prefers the
# version-less "releases/latest/download/<asset>" pattern over a per-version URL.
choose_asset() {
  local latest_base="https://github.com/${REPO}/releases/latest/download"
  local asset
  for asset in "$DMG_ASSET" "$PKG_ASSET" "$ZIP_ASSET"; do
    if curl -fsSIL -o /dev/null --max-time 30 "${latest_base}/${asset}" >/dev/null 2>&1; then
      URL="${latest_base}/${asset}"
      case "$asset" in
        *.dmg) ASSET_KIND=dmg ;;
        *.pkg) ASSET_KIND=pkg ;;
        *.zip) ASSET_KIND=zip ;;
        *)     ASSET_KIND=other ;;
      esac
      return 0
    fi
  done
  return 1
}

# Install a .dmg. Uses the caller's TMPDIR_OUT so the parent's EXIT trap remains the
# single owner of cleanup; this function installs no conflicting trap.
install_dmg() {
  local dmg="$1" mount app_src staged_app
  mount="${TMPDIR_OUT}/mount"
  # hdiutil requires an existing mountpoint; create a fresh one under the temp dir.
  mkdir -p "$mount"
  hdiutil attach -nobrowse -quiet -mountpoint "$mount" "$dmg"
  app_src=$(find "$mount" -maxdepth 2 -iname '*.app' -print -quit 2>/dev/null || true)
  if [ -z "$app_src" ]; then
    hdiutil detach "$mount" -quiet 2>/dev/null || true
    echo "error: no .app bundle found inside the mounted disk image" >&2
    return 1
  fi
  # Copy the bundle into the writable temp dir BEFORE detaching so the mounted path is
  # no longer needed for the install step.
  mkdir -p "${TMPDIR_OUT}/app"
  cp -R "$app_src" "${TMPDIR_OUT}/app/"
  hdiutil detach "$mount" -quiet 2>/dev/null || true
  # Replace the installed bundle: copy the single staged .app into /Applications renamed
  # to the requested --appName so it lands at /Applications/<appName>.app, not nested.
  rm -rf "$APP_TARGET"
  mkdir -p "$(dirname "$APP_TARGET")"
  staged_app=$(find "${TMPDIR_OUT}/app" -maxdepth 1 -iname '*.app' -print -quit 2>/dev/null || true)
  if [ -z "$staged_app" ]; then
    echo "error: no .app to install (staging empty) for ${APP_TARGET}" >&2
    return 1
  fi
  cp -R "$staged_app" "$APP_TARGET"
  # Very likely a Developer-ID/unsigned build, not an App Store one, so strip the
  # quarantine attribute that would otherwise require interactive approval to run.
  xattr -dr com.apple.quarantine "$APP_TARGET" 2>/dev/null || true
  return 0
}

# Install a .pkg package.
install_pkg() {
  local pkg="$1"
  installer -pkg "$pkg" -target /
}

# Install a .zip: prefer an .app bundle, fall back to a .pkg inside the archive.
install_zip() {
  local zip="$1" extracted app_src pkg_src
  extracted="${TMPDIR_OUT}/extracted"
  ditto -x -k "$zip" "$extracted"
  app_src=$(find "$extracted" -maxdepth 3 -iname '*.app' -print -quit 2>/dev/null || true)
  if [ -n "$app_src" ]; then
    rm -rf "$APP_TARGET"
    mkdir -p "$(dirname "$APP_TARGET")"
    cp -R "$app_src" "$APP_TARGET"
    xattr -dr com.apple.quarantine "$APP_TARGET" 2>/dev/null || true
    return 0
  fi
  pkg_src=$(find "$extracted" -maxdepth 3 -iname '*.pkg' -print -quit 2>/dev/null || true)
  if [ -n "$pkg_src" ]; then
    installer -pkg "$pkg_src" -target /
    return 0
  fi
  echo "error: archive contained no recognizable .app or .pkg" >&2
  return 1
}

# ---- Mode: --update-version (pure HTTP check, OS-independent) --------------------
if [ "$MODE" = "update-version" ]; then
  LATEST=$(fetch_latest_version)
  printf '%s\n' "$LATEST"
  exit 0
fi

# ---- Mode: --update (runs on the managed macOS host) ----------------------------
# 1) Latest version.
LATEST=$(fetch_latest_version)

# 2) Installed version + bundle-identity verification before touching anything.
INSTALLED=""
if [ -d "$APP_TARGET" ]; then
  INSTALLED=$(read_installed_version)
  INST_ID=$(read_installed_id)
  if [ -z "$INST_ID" ]; then
    echo "error: could not read CFBundleIdentifier of ${APP_TARGET}" >&2
    exit 1
  fi
  if [ "$INST_ID" != "$APP_ID" ]; then
    echo "error: installed bundle identifier '${INST_ID}' does not match expected '${APP_ID}' (wrong app); aborting" >&2
    exit 1
  fi
else
  INST_ID=""
fi

# 3) Idempotency: already at or ahead of latest -> no-op (no download, no change).
if [ -n "$INSTALLED" ]; then
  if ver_ge "$INSTALLED" "$LATEST"; then
    echo "calibre-agent already at or ahead of latest ${LATEST} (installed ${INSTALLED}); no update needed"
    exit 0
  fi
else
  echo "calibre-agent not currently installed; installing latest ${LATEST}"
fi

# 4) Gracefully quit if running.
if pgrep -x "$APP_NAME" >/dev/null 2>&1; then
  quit_app_gracefully
fi

# 5) Download the current release to a temp dir (trap-cleaned on success or failure).
TMPDIR_OUT=$(mktemp -d)
trap 'rm -rf "${TMPDIR_OUT}"' EXIT INT TERM

# Pick the distribution asset via the stable version-less "latest/download/<asset>" URL.
URL=""
ASSET_KIND=""
if ! choose_asset; then
  echo "error: no known macOS release asset found for ${REPO}" >&2
  exit 1
fi

DEST="${TMPDIR_OUT}/$(basename "${URL}")"
echo "downloading ${URL}"
curl -fsL --max-time 600 -o "$DEST" "$URL"
if [ ! -s "$DEST" ]; then
  echo "error: download produced an empty file" >&2
  exit 1
fi

# 6) Install according to the asset form.
case "$ASSET_KIND" in
  dmg) install_dmg "$DEST" ;;
  pkg) install_pkg "$DEST" ;;
  zip) install_zip "$DEST" ;;
  *)
    echo "error: unknown distribution form for ${ASSET_KIND}" >&2
    exit 1 ;;
esac

# 7) Verify the installed version now meets the target.
NEWVER=$(read_installed_version || true)
if [ -z "$NEWVER" ]; then
  echo "error: could not read installed version after update" >&2
  exit 1
fi
if ! ver_ge "$NEWVER" "$LATEST"; then
  echo "error: after update installed version is ${NEWVER} but latest is ${LATEST}" >&2
  exit 1
fi
echo "calibre-agent updated to ${NEWVER} (latest ${LATEST})"
exit 0
