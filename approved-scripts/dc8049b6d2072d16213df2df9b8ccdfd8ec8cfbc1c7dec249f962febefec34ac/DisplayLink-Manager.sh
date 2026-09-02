#!/bin/bash
set -euo pipefail

# DisplayLink Manager (com.displaylink.DisplayLinkUserAgent) -- Synaptics/DisplayLink.
#
# Distribution: there is no auto-updater, no Sparkle feed, no GitHub project and no
# stable "latest" download URL. The only official channel is the Synaptics downloads
# page, which lists every release as a pair of links: a per-version product page
# carrying the .pkg URL, and a release-notes .txt. So the discovery chain is:
#
#   1. downloads page  -> highest "DisplayLink Manager Graphics Connectivity<ver>-Release Notes.txt"
#   2. that .txt       -> "Version: 16.2.39"  (the full version, matching CFBundleShortVersionString)
#   3. product page    -> /sites/default/files/exe_files/<date>/...-EXE.pkg
#
# Step 2 matters: the page and the notes *filename* only carry the marketing version
# ("16.2"), while the installed bundle reports "16.2.39". Reporting the marketing
# version would leave the two permanently unequal. Step 3 must be scraped rather than
# constructed: the pkg's date directory does not always equal the notes' date
# directory (1.9's notes are under 2023-09, its pkg under 2023-07), so guessing it
# 404s on precisely the releases where a guess would be needed.
#
# WARNING: researched against the live Synaptics site; the version/URL patterns above
# were verified by fetching them, and the pkg was expanded to confirm it installs
# "/Applications/DisplayLink Manager.app" with CFBundleIdentifier
# com.displaylink.DisplayLinkUserAgent and CFBundleShortVersionString 16.2.39. Two
# limitations are inherent rather than bugs:
#   * Current releases (16.x) declare minimumSystemVersion 14.0 and Synaptics lists
#     them as Sonoma-or-newer. On an older macOS, `installer` will refuse; Synaptics
#     publishes a separate, frozen line for those (1.9 / 1.11 / 14.2). This mode
#     reports one fleet-wide version and structurally cannot branch per host OS, so
#     older Macs are expected to report a failed --update rather than be handled here.
#   * The latest version is the numeric maximum of the release-notes filenames. If
#     Synaptics ever lists a beta under a plain numeric name it would be picked up;
#     historically betas are not listed on this page.

readonly SITE="https://www.synaptics.com"
readonly DOWNLOADS_PAGE="${SITE}/products/displaylink-graphics/downloads/macos"
# The pkg installs here regardless of what --appName the fleet happens to pass; used
# only as a fallback, and only once CFBundleIdentifier has confirmed the bundle.
readonly KNOWN_APP_PATH="/Applications/DisplayLink Manager.app"
# The bundle's CFBundleExecutable, i.e. the actual process name -- *not* the app name.
# `pgrep -x "DisplayLink Manager"` would never match anything.
readonly KNOWN_EXECUTABLE="DisplayLinkUserAgent"
# Apple Developer team ID the installer packages are signed with, checked before
# anything is handed to `installer`, which runs as root. A team ID is the stable half
# of the signing identity -- it survives certificate renewal, where the certificate's
# own name and fingerprint do not. If DisplayLink ever re-signs under a different team
# (an acquisition, say), --update fails loudly here rather than installing whatever the
# page happened to serve; that is the right direction for a root-level install, but it
# is the one line a future reader may legitimately need to change.
readonly EXPECTED_TEAM_ID="73YQY62QM3"

usage() {
    echo "usage: $(basename "$0") --appName <name> --appId <bundle-id> (--update-version | --update)" >&2
    exit 2
}

die() {
    echo "error: $*" >&2
    exit 1
}

fetch() {
    # --retry covers the transient failures a fleet-wide poll will otherwise hit
    # regularly; no -L is needed, these URLs do not redirect.
    curl -fsS --retry 3 --retry-delay 2 --max-time 120 "$1"
}

# version_ge <a> <b> -- true when version a >= version b, comparing dot-separated
# components numerically with missing components treated as 0, so "16.2.39" >= "16.2".
# Written for bash 3.2, which is what macOS ships.
version_ge() {
    local a="$1" b="$2" i max x y
    local -a av bv
    # The IFS prefix applies to `read` alone, so nothing leaks into the caller.
    IFS=. read -r -a av <<< "$a"
    IFS=. read -r -a bv <<< "$b"
    max=${#av[@]}
    [ "${#bv[@]}" -gt "$max" ] && max=${#bv[@]}
    i=0
    while [ "$i" -lt "$max" ]; do
        x=${av[$i]:-0}
        y=${bv[$i]:-0}
        # strip anything non-numeric (a trailing build suffix) and force base 10 so a
        # zero-padded component is not read as octal
        x=$(printf '%s' "$x" | tr -cd '0-9')
        y=$(printf '%s' "$y" | tr -cd '0-9')
        x=$((10#${x:-0}))
        y=$((10#${y:-0}))
        [ "$x" -gt "$y" ] && return 0
        [ "$x" -lt "$y" ] && return 1
        i=$((i + 1))
    done
    return 0
}

# Prints "<marketing-version> <release-notes-url>" for the newest release listed.
discover_latest_release() {
    local page notes_paths path ver best_ver="" best_path=""

    page=$(fetch "$DOWNLOADS_PAGE") || die "cannot reach the Synaptics downloads page ($DOWNLOADS_PAGE)"

    # Every extraction below is captured with `|| true` and then tested for emptiness:
    # grep exits 1 when it matches nothing, which under `set -e` would kill the script
    # before it could report anything.
    notes_paths=$(printf '%s' "$page" | grep -oE '/sites/default/files/release_notes/[0-9]{4}-[0-9]{2}/DisplayLink%20Manager%20Graphics%20Connectivity[0-9]+(\.[0-9]+)*-Release%20Notes\.txt' | sort -u || true)
    [ -n "$notes_paths" ] || die "no DisplayLink Manager releases found on $DOWNLOADS_PAGE (page layout may have changed)"

    while IFS= read -r path; do
        [ -n "$path" ] || continue
        ver=$(printf '%s' "$path" | sed -E 's#.*Connectivity([0-9]+(\.[0-9]+)*)-Release%20Notes\.txt$#\1#' || true)
        case "$ver" in
            ''|*[!0-9.]*) continue ;;
        esac
        if [ -z "$best_ver" ] || version_ge "$ver" "$best_ver"; then
            best_ver="$ver"
            best_path="$path"
        fi
    done <<EOF
$notes_paths
EOF

    [ -n "$best_ver" ] || die "could not parse any version out of the release notes links on $DOWNLOADS_PAGE"
    echo "$best_ver ${SITE}${best_path}"
}

# Prints the full latest version, e.g. "16.2.39". Takes an already-discovered
# "<marketing-version> <notes-url>" pair when the caller has one: --update needs both
# halves, and re-discovering would fetch the page twice and could straddle a release,
# leaving the version it verifies against different from the one it downloaded.
latest_version() {
    local release marketing notes_url notes full

    if [ "$#" -ge 1 ] && [ -n "$1" ]; then
        release="$1"
    else
        release=$(discover_latest_release)
    fi
    marketing=${release%% *}
    notes_url=${release#* }

    # The release notes carry the full four-figure version the bundle reports. If they
    # cannot be read or parsed, fall back to the marketing version from the filename --
    # coarser, but still correct as a lower bound, and --update compares with >=.
    notes=$(fetch "$notes_url" || true)
    full=$(printf '%s' "$notes" | tr -d '\r' | grep -m1 -iE '^[[:space:]]*Version:' | sed -E 's/.*[Vv]ersion:[[:space:]]*//' | grep -oE '^[0-9]+(\.[0-9]+)*' || true)

    # Guard against having parsed some unrelated line: the full version must extend the
    # marketing version.
    case "$full" in
        "$marketing"|"$marketing".*) echo "$full"; return 0 ;;
    esac
    echo "$marketing"
}

# Prints the absolute download URL for a given marketing version, scraped from that
# version's own product page.
download_url() {
    local marketing="$1" slug product_page page path

    # The product page slug is the version with the dots removed: 16.2 -> 162,
    # 1.12.4 -> 1124.
    slug=$(printf '%s' "$marketing" | tr -d '.')
    product_page="${SITE}/products/displaylink-manager-graphics-connectivity-${slug}?filetype=exe"

    page=$(fetch "$product_page") || die "cannot reach the product page for $marketing ($product_page)"
    path=$(printf '%s' "$page" | grep -oE '/sites/default/files/exe_files/[^"'"'"' >]+\.(pkg|zip|dmg)' | head -1 || true)
    [ -n "$path" ] || die "no installer link found on $product_page (page layout may have changed)"
    echo "${SITE}${path}"
}

bundle_value() {
    # `defaults read` on an explicit plist path -- works for both binary and XML plists
    # and needs no user context.
    defaults read "$1/Contents/Info.plist" "$2" 2>/dev/null || true
}

quit_if_running() {
    local app_path="$1" app_name="$2" executable waited

    executable=$(bundle_value "$app_path" CFBundleExecutable)
    [ -n "$executable" ] || executable="$KNOWN_EXECUTABLE"

    pgrep -x "$executable" >/dev/null 2>&1 || return 0

    echo "Quitting $app_name (process $executable) before replacing it"
    osascript -e "tell application \"${app_name}\" to quit" >/dev/null 2>&1 || true

    waited=0
    while [ "$waited" -lt 15 ]; do
        pgrep -x "$executable" >/dev/null 2>&1 || return 0
        sleep 1
        waited=$((waited + 1))
    done

    # Last resort only. The bundle ships a CrashRestartHelper that may bring the agent
    # straight back after a hard kill; that is harmless here because `installer`
    # overwrites the bundle either way, and the pkg's postinstall reloads the agents.
    echo "warning: $executable did not exit within 15s; terminating it" >&2
    pkill -x "$executable" >/dev/null 2>&1 || true
    sleep 2
}

verify_signature() {
    local file="$1" out
    out=$(pkgutil --check-signature "$file" 2>&1) || die "$(basename "$file") carries no usable signature"
    # Matched with a case rather than `grep -q`: grep exits on the first match and can
    # SIGPIPE the writer, which under `pipefail` would look like a failed check.
    case "$out" in
        *"Developer ID Installer: "*"(${EXPECTED_TEAM_ID})"*) ;;
        *) die "$(basename "$file") is not signed by Developer ID team ${EXPECTED_TEAM_ID}; refusing to install it" ;;
    esac
}

install_pkg() {
    verify_signature "$1"
    echo "Installing $(basename "$1")"
    # The Distribution requests a restart (requireRestart) for the display driver
    # extension. The app bundle on disk is replaced immediately, so the version check
    # below passes without rebooting; do not "fix" that by adding one.
    installer -pkg "$1" -target / >/dev/null || die "installer failed for $1"
}

install_app_bundle() {
    local src="$1" dest="$2" out
    # Same gate as install_pkg, for the contingency branches: nothing unsigned, or
    # signed by anyone else, gets copied into /Applications.
    codesign --verify --strict "$src" >/dev/null 2>&1 || die "$(basename "$src") fails code-signature verification; refusing to install it"
    out=$(codesign -dvv "$src" 2>&1) || die "cannot read the code signature of $(basename "$src")"
    case "$out" in
        *"TeamIdentifier=${EXPECTED_TEAM_ID}"*) ;;
        *) die "$(basename "$src") is not signed by team ${EXPECTED_TEAM_ID}; refusing to install it" ;;
    esac
    echo "Copying $(basename "$src") into $(dirname "$dest")"
    rm -rf "$dest"
    cp -R "$src" "$dest" || die "could not copy $src to $dest"
    # Developer-ID distribution rather than App Store, so clear the quarantine flag.
    xattr -dr com.apple.quarantine "$dest" 2>/dev/null || true
}

install_from_download() {
    local file="$1" workdir="$2" app_path="$3" inner mountpoint

    case "$file" in
        *.pkg)
            install_pkg "$file"
            ;;
        *.zip)
            # Contingency: some releases in this line (e.g. 14.2) ship the pkg zipped.
            mkdir -p "$workdir/unzipped"
            /usr/bin/unzip -qq -o "$file" -d "$workdir/unzipped" || die "could not unzip $file"
            inner=$(/usr/bin/find "$workdir/unzipped" -maxdepth 3 -name '*.pkg' -print 2>/dev/null | head -1 || true)
            if [ -n "$inner" ]; then
                install_pkg "$inner"
            else
                inner=$(/usr/bin/find "$workdir/unzipped" -maxdepth 3 -name '*.app' -print 2>/dev/null | head -1 || true)
                [ -n "$inner" ] || die "no .pkg or .app found inside $file"
                install_app_bundle "$inner" "$app_path"
            fi
            ;;
        *.dmg)
            # Contingency only; this line has never shipped a dmg.
            mountpoint="$workdir/mnt"
            mkdir -p "$mountpoint"
            hdiutil attach -nobrowse -quiet -mountpoint "$mountpoint" "$file" || die "could not mount $file"
            inner=$(/usr/bin/find "$mountpoint" -maxdepth 2 -name '*.pkg' -print 2>/dev/null | head -1 || true)
            if [ -n "$inner" ]; then
                cp "$inner" "$workdir/from-dmg.pkg" || { hdiutil detach "$mountpoint" -quiet || true; die "could not copy pkg out of $file"; }
                hdiutil detach "$mountpoint" -quiet || true
                install_pkg "$workdir/from-dmg.pkg"
            else
                inner=$(/usr/bin/find "$mountpoint" -maxdepth 2 -name '*.app' -print 2>/dev/null | head -1 || true)
                if [ -z "$inner" ]; then
                    hdiutil detach "$mountpoint" -quiet || true
                    die "no .pkg or .app found inside $file"
                fi
                cp -R "$inner" "$workdir/$(basename "$inner")" || { hdiutil detach "$mountpoint" -quiet || true; die "could not copy app out of $file"; }
                hdiutil detach "$mountpoint" -quiet || true
                install_app_bundle "$workdir/$(basename "$inner")" "$app_path"
            fi
            ;;
        *)
            die "unrecognised installer form: $file"
            ;;
    esac
}

do_update() {
    local app_name="$1" app_id="$2"
    local release marketing latest app_path installed found_id url workdir filename

    release=$(discover_latest_release)
    marketing=${release%% *}
    latest=$(latest_version "$release")

    app_path="/Applications/${app_name}.app"
    if [ ! -d "$app_path" ] && [ -d "$KNOWN_APP_PATH" ]; then
        app_path="$KNOWN_APP_PATH"
    fi

    if [ -d "$app_path" ]; then
        found_id=$(bundle_value "$app_path" CFBundleIdentifier)
        [ -n "$found_id" ] || die "cannot read CFBundleIdentifier from $app_path"
        # Wrong app at this path is fatal, never something to install over.
        [ "$found_id" = "$app_id" ] || die "$app_path has CFBundleIdentifier '$found_id', expected '$app_id' -- refusing to touch it"
        installed=$(bundle_value "$app_path" CFBundleShortVersionString)
        [ -n "$installed" ] || die "cannot read CFBundleShortVersionString from $app_path"
    else
        # Nothing installed: there is no bundle for the identifier guard to compare, and
        # the job is to bring the host current, so install.
        echo "$app_name is not installed; installing $latest"
        app_path="/Applications/${app_name}.app"
        installed="0"
    fi

    if version_ge "$installed" "$latest"; then
        echo "$app_name $installed is already at or above the latest release ($latest); nothing to do"
        return 0
    fi

    echo "$app_name $installed is older than the latest release ($latest); updating"

    url=$(download_url "$marketing")

    workdir=$(mktemp -d) || die "could not create a temporary directory"
    # Covers success and every failure path, including die().
    trap 'rm -rf "$workdir"' EXIT

    filename=$(printf '%s' "$url" | sed -E 's#.*/##; s/%20/ /g')
    case "$filename" in
        *.pkg|*.zip|*.dmg) ;;
        *) filename="DisplayLinkManager.pkg" ;;
    esac

    echo "Downloading $url"
    curl -fL --retry 3 --retry-delay 2 --max-time 900 -o "$workdir/$filename" "$url" \
        || die "download failed: $url"

    quit_if_running "$app_path" "$app_name"
    install_from_download "$workdir/$filename" "$workdir" "$app_path"

    # The pkg installs to its own fixed path whatever --appName said, so a fresh
    # install may not land where the name suggested. Fall back exactly as the
    # pre-check does -- reporting a successful install as a failure is the worst
    # outcome available to a fleet, since it invites a retry loop.
    if [ ! -d "$app_path" ] && [ -d "$KNOWN_APP_PATH" ]; then
        app_path="$KNOWN_APP_PATH"
    fi
    [ -d "$app_path" ] || die "no installed bundle found after installation (looked in /Applications/${app_name}.app and $KNOWN_APP_PATH)"
    found_id=$(bundle_value "$app_path" CFBundleIdentifier)
    # Keeps the fallback as guarded as the pre-check: the identifier is what makes
    # using a path the caller did not name safe.
    [ "$found_id" = "$app_id" ] || die "$app_path has CFBundleIdentifier '$found_id' after installation, expected '$app_id'"
    installed=$(bundle_value "$app_path" CFBundleShortVersionString)
    [ -n "$installed" ] || die "cannot read the installed version from $app_path after installation"
    version_ge "$installed" "$latest" \
        || die "installation completed but $app_path reports $installed, expected at least $latest"

    echo "$app_name is now at $installed"
}

app_name=""
app_id=""
mode=""

while [ "$#" -gt 0 ]; do
    case "$1" in
        --appName)
            [ "$#" -ge 2 ] || usage
            [ -z "$app_name" ] || usage
            app_name="$2"
            shift 2
            ;;
        --appId)
            [ "$#" -ge 2 ] || usage
            [ -z "$app_id" ] || usage
            app_id="$2"
            shift 2
            ;;
        --update-version)
            [ -z "$mode" ] || usage
            mode="version"
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

case "$mode" in
    version) latest_version ;;
    update) do_update "$app_name" "$app_id" ;;
esac
