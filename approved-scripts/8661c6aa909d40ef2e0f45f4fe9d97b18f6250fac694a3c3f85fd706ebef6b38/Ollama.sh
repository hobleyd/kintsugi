#!/bin/bash
set -euo pipefail

# Upgrade tool for Ollama (com.electron.ollama) on macOS.
#
# Distribution: Ollama is open source and hosted at github.com/ollama/ollama. The macOS
# build ships as a single asset, "Ollama.dmg", attached to every stable GitHub release; the
# disk image contains Ollama.app plus a drag-to-/Applications symlink. There is no Sparkle
# feed, no pkg, and no Mac App Store presence, so replacing the bundle is the supported
# unattended path -- it is what the app's own in-place updater does.
#
# Version discovery is deliberately release-tag-based rather than baked in:
#   https://github.com/ollama/ollama/releases/latest  302s to .../releases/tag/vX.Y.Z
# and the tag with its leading "v" stripped is byte-identical to the installed bundle's
# CFBundleShortVersionString (verified: tag v0.33.2 <-> bundle 0.33.2). That keeps working
# for releases that do not exist yet, which is the whole point of this script.
#
# Two details of that trick are load-bearing:
#   * No -L. %{redirect_url} reports the redirect curl did NOT follow; adding -L makes curl
#     follow it and report an empty string, which would look like "no update available"
#     rather than an error. Ollama has shipped exactly that class of silent failure before.
#   * GitHub's notion of "latest" excludes prereleases, and Ollama publishes them
#     (v0.33.3-rc0 existed while v0.33.2 was latest). So the redirect gives the latest
#     *stable* release for free; parsing the releases list would not.
#
# The download uses the stable /releases/latest/download/Ollama.dmg URL rather than a
# per-version URL built from the discovered string, so the asset is fetched from whatever
# GitHub currently considers latest. The filename "Ollama.dmg" is the one hardcoded fact
# here; if it were ever renamed, curl -f fails the run loudly instead of quietly doing
# nothing, which is the behaviour we want.

usage() {
    echo "usage: $0 --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
    exit 2
}

app_name=""
app_id=""
mode=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --appName)
            # ${2:-} rather than "$2": under set -u a value-taking flag arriving last
            # ("--appId x --update --appName") would otherwise abort with bash's own unbound
            # variable error instead of this script's one-line usage. The --* test guards
            # against a missing value swallowing the next flag ("--appName --update").
            [[ -n "${2:-}" && "${2:-}" != --* ]] || usage
            app_name="$2"
            shift 2
            ;;
        --appId)
            [[ -n "${2:-}" && "${2:-}" != --* ]] || usage
            app_id="$2"
            shift 2
            ;;
        --update-version|--update)
            [[ -z "$mode" ]] || usage   # exactly one mode, never both
            mode="$1"
            shift
            ;;
        *)
            usage
            ;;
    esac
done

[[ -n "$app_name" && -n "$app_id" && -n "$mode" ]] || usage

readonly RELEASES_LATEST_URL="https://github.com/ollama/ollama/releases/latest"
readonly DMG_URL="https://github.com/ollama/ollama/releases/latest/download/Ollama.dmg"

# Resolve the latest stable version. Runs on a plain Linux server in --update-version mode,
# so: curl and text processing only, no macOS tools, no filesystem writes, no jq.
latest_version() {
    local redirect tag

    redirect="$(curl -fsS --retry 3 --retry-delay 2 --max-time 60 \
        -o /dev/null -w '%{redirect_url}' "$RELEASES_LATEST_URL")" || {
        echo "error: could not reach $RELEASES_LATEST_URL" >&2
        return 1
    }

    # .../releases/tag/v0.33.2 -> 0.33.2
    tag="$(printf '%s' "$redirect" | sed -e 's#.*/##' -e 's/^[vV]//' | tr -d '[:space:]')"

    # Fail loudly on anything unparseable. An empty or malformed answer must never leave
    # this function as a successful empty stdout: downstream, a null latest version reads
    # as "no update available", so the failure would silently stop this app ever patching.
    if [[ ! "$tag" =~ ^[0-9]+(\.[0-9]+)+$ ]]; then
        echo "error: could not parse a version from redirect '$redirect'" >&2
        return 1
    fi

    printf '%s\n' "$tag"
}

# True when $1 (installed) is greater than or equal to $2 (latest).
version_at_least() {
    [[ "$1" == "$2" ]] && return 0
    # Lowest of the pair sorts first; if that is the latest, the installed one is ahead.
    [[ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)" == "$2" ]]
}

# Read one Info.plist key. PlistBuddy rather than `defaults read`: `defaults` goes through
# cfprefsd, which can hand back a cached value for a path whose contents just changed on
# disk -- and this script reads the same bundle path immediately after replacing it, where a
# stale read would report a false success or a false failure.
plist_value() {
    /usr/libexec/PlistBuddy -c "Print :$2" "$1/Contents/Info.plist" 2>/dev/null | tr -d '[:space:]'
}

if [[ "$mode" == "--update-version" ]]; then
    latest_version
    exit 0
fi

# ---------------------------------------------------------------------------
# --update: runs on the managed Mac, so macOS tooling is fair game from here.
# ---------------------------------------------------------------------------

readonly APP_PATH="/Applications/${app_name}.app"

# Identify the installed bundle before anything else is touched. A missing bundle is fatal
# rather than a fresh install: with no Info.plist there is nothing to check --appId against,
# and this tool's job is upgrading a known install, not deploying to an unmanaged host.
if [[ ! -d "$APP_PATH" ]]; then
    echo "error: $APP_PATH is not installed" >&2
    exit 1
fi

installed_id="$(plist_value "$APP_PATH" CFBundleIdentifier)"
if [[ "$installed_id" != "$app_id" ]]; then
    echo "error: $APP_PATH has CFBundleIdentifier '$installed_id', expected '$app_id'" >&2
    exit 1
fi

installed_version="$(plist_value "$APP_PATH" CFBundleShortVersionString)"
if [[ -z "$installed_version" ]]; then
    echo "error: could not read CFBundleShortVersionString from $APP_PATH" >&2
    exit 1
fi

latest="$(latest_version)"

if version_at_least "$installed_version" "$latest"; then
    echo "${app_name} ${installed_version} is already at or above the latest release (${latest}); nothing to do."
    exit 0
fi

echo "${app_name} ${installed_version} is out of date; updating to ${latest}."

# Quit the app gracefully if it is running. Already authorized, so no prompting -- but ask
# first and wait, because Ollama may be mid-inference and a hard kill can leave a partial
# model blob behind. pkill is the last resort after the grace period, not the opener.
#
# pgrep -x matches the GUI process only, and deliberately: quitting the app takes its
# lowercase "ollama serve" child with it, and any stray server process holding the old
# binary open does not block the replacement -- Unix unlinks a running image happily. Do
# not widen this to pattern-match "ollama", which would also match an unrelated
# Homebrew-installed server this tool has no business terminating.
if pgrep -x "$app_name" >/dev/null 2>&1; then
    echo "Quitting running ${app_name}..."
    osascript -e "tell application \"${app_name}\" to quit" >/dev/null 2>&1 || true
    for _ in $(seq 1 15); do
        pgrep -x "$app_name" >/dev/null 2>&1 || break
        sleep 1
    done
    if pgrep -x "$app_name" >/dev/null 2>&1; then
        echo "warning: ${app_name} did not quit within 15s; terminating it." >&2
        pkill -x "$app_name" || true
        sleep 2
    fi
fi

work_dir="$(mktemp -d)"
mount_point="${work_dir}/mnt"
staged_app=""

# Cleanup must detach before removing the work dir, or the still-mounted image keeps the
# directory busy and leaves the volume attached. Idempotent, and used on both paths.
cleanup() {
    if [[ -d "$mount_point" ]] && /usr/bin/hdiutil info | grep -qF "$mount_point"; then
        # "resource busy" on the first detach is routine (Spotlight, Finder, an antivirus
        # scanner); retry with -force rather than leaving the image attached forever.
        hdiutil detach "$mount_point" -quiet 2>/dev/null || {
            sleep 3
            hdiutil detach "$mount_point" -force -quiet 2>/dev/null || true
        }
    fi
    [[ -n "$staged_app" && -d "$staged_app" ]] && rm -rf "$staged_app"
    rm -rf "$work_dir"
}
trap cleanup EXIT

dmg="${work_dir}/Ollama.dmg"
echo "Downloading ${DMG_URL}..."
curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 30 --max-time 1800 -o "$dmg" "$DMG_URL" || {
    echo "error: failed to download $DMG_URL" >&2
    exit 1
}

mkdir -p "$mount_point"
hdiutil attach -nobrowse -quiet -mountpoint "$mount_point" "$dmg" || {
    echo "error: failed to mount $dmg" >&2
    exit 1
}

# Find the bundle with a depth-1 glob, never a recursive find: the image's root holds an
# "Applications -> /Applications" symlink, and a following search would enumerate the whole
# host application folder and pick up the copy we are trying to replace.
source_app=""
for candidate in "$mount_point"/*.app; do
    [[ -d "$candidate" ]] || continue
    if [[ "$(plist_value "$candidate" CFBundleIdentifier)" == "$app_id" ]]; then
        source_app="$candidate"
        break
    fi
done

if [[ -z "$source_app" ]]; then
    echo "error: no .app with CFBundleIdentifier '$app_id' found in $dmg" >&2
    exit 1
fi

# Stage beside the target, then swap. Copying straight into /Applications would nest the new
# bundle inside the existing one (/Applications/Ollama.app/Ollama.app), and staging on the
# same volume keeps the window in which no app exists down to two renames.
staged_app="/Applications/.${app_name}.app.kintsugi-$$"
rm -rf "$staged_app"
# ditto, not cp -R: it is the Apple-sanctioned bundle copy and preserves the extended
# attributes, resource forks and ACLs a signed Electron bundle carries. A copy that loses
# any of those leaves a bundle whose Info.plist reads perfectly and whose signature is
# broken, which Gatekeeper only reveals at first launch -- long after this script has
# reported success.
ditto "$source_app" "$staged_app"

# Developer-ID distribution outside the App Store, so the copy inherits a quarantine flag
# that would make the app refuse to launch unattended.
xattr -dr com.apple.quarantine "$staged_app" 2>/dev/null || true

# Verify the copy before the swap, while the working install is still in place: a damaged
# signature found here costs nothing, whereas the same fault found after the swap has
# already replaced a working app with one that will not launch.
if ! codesign --verify --strict "$staged_app" 2>/dev/null; then
    echo "error: copied bundle at $staged_app fails signature verification; leaving $APP_PATH untouched" >&2
    exit 1
fi

rm -rf "$APP_PATH"
mv "$staged_app" "$APP_PATH"
staged_app=""

# Detach now rather than waiting for the trap, so a detach problem is reported as itself.
hdiutil detach "$mount_point" -quiet 2>/dev/null || {
    sleep 3
    hdiutil detach "$mount_point" -force -quiet 2>/dev/null || true
}

# Deliberately not relaunched: this typically runs from a root context with no user session
# to launch into, and nothing here requires the app to be up. Ollama's /usr/local/bin/ollama
# symlink points at a path inside the bundle that the replacement recreates, so the CLI keeps
# working without intervention. Do not "fix" this by adding an `open -a`.

new_version="$(plist_value "$APP_PATH" CFBundleShortVersionString)"
new_id="$(plist_value "$APP_PATH" CFBundleIdentifier)"

if [[ "$new_id" != "$app_id" ]]; then
    echo "error: installed bundle reports CFBundleIdentifier '$new_id', expected '$app_id'" >&2
    exit 1
fi

if [[ -z "$new_version" ]] || ! version_at_least "$new_version" "$latest"; then
    echo "error: ${app_name} reports version '${new_version}' after updating, expected ${latest} or newer" >&2
    exit 1
fi

echo "${app_name} updated from ${installed_version} to ${new_version}."
