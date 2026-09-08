#!/bin/bash
# Builds the release binary, bundles it with everything a brand-new install needs (config.toml,
# all three LaunchDaemon/LaunchAgent plists, install.sh, uninstall.sh, and kintsugi-mas — the same set
# dist/ has always held as kintsugi-agent-macos-installer.tar.gz), and publishes that one bundle to
# the server.
#
# It does double duty: a human downloads it from the Clients page for a fresh install, and an
# already-enrolled agent's own auto-update check downloads the very same file and just extracts
# the "kintsugi-agent" (and "kintsugi-mas") entries out of it, ignoring the rest (see
# self_update.rs's extraction) — so there's only ever one artifact to build and publish, not two.
#
# The bundled config.toml's enrollment_token is left blank here on purpose: the server rewrites it
# to whatever AGENT_ENROLLMENT_TOKEN currently is on every download request, not just once at
# publish time — see AgentPackageArchiveRewriter — so a token rotation never makes an already-
# published package stale, and there's no --enrollment-token flag to remember to pass here.
#
#   packaging/publish-release.sh
#   packaging/publish-release.sh --api-base-url https://kintsugi.example.com:8443
#   packaging/publish-release.sh --release-notes "Fixes the menu bar version label"
#   packaging/publish-release.sh --mas-binary /path/to/universal/mas   # offline; skips the download
#   packaging/publish-release.sh --ad-hoc            # a throwaway build; orphans every TCC grant
#
# Everything it packages is code-signed with the fleet identity when one is reachable — one
# certificate, minted once by packaging/create-signing-identity.sh. That is not packaging tidiness:
# the designated requirement of an ad-hoc signature is a hash of that exact build, and TCC records
# it verbatim, so an ad-hoc package orphans the Screen Recording and Accessibility grants remote
# control needs on every host that installs it, and again on its next self-update.
#
# **The fleet's key lives in this repository's GitHub Actions secrets and nowhere else**, because
# the release job is the only thing that builds the binary hosts install and self-update to. So a
# hand-run publish from a machine that has no such identity signs ad hoc, says so, and carries on:
# what it produces is a package for one server, and the person reading the warning can decide.
# CI, where nobody is reading, fails instead. --ad-hoc says you meant it.
#
# The version published is always this crate's own Cargo.toml version — bump that first. Run from
# a plain (non-root) shell; unlike install.sh this never needs sudo, since it's talking to the
# server over the network, not touching this machine's launchd/filesystem.
#
# CI builds the binary itself — a universal one on macOS, a static musl one on Linux, neither of
# which a plain `cargo build --release` on the build host produces — and has no route to anyone's
# server. So both halves of this script are separable: --binary packages an already-built binary
# instead of building one, and --output-dir writes the tarball to a directory and stops before
# publishing. The tar invocation below stays the single owner of the archive's top-level entry
# names either way, because those names are what self_update.rs extracts by — reimplementing the
# `tar` call in a workflow file would let the two drift apart silently. See
# .github/workflows/ci.yml.
set -euo pipefail

# The archive carries the agent's own copy of mas (https://github.com/mas-cli/mas), which is the
# only thing that can update a Mac App Store app from a script: the server's AppStoreUpgradeScript
# runs /usr/local/bin/kintsugi-mas as root inside the console user's session (see config.rs's
# MAS_BINARY_PATH and the script itself for the whole arrangement). It has to be *our* root-owned
# copy because a root daemon that ran Homebrew's user-writable /opt/homebrew/bin/mas would be root
# for whoever owns that prefix. mas publishes one .pkg per architecture and no universal build, so
# both are fetched, pinned by digest, and lipo'd into one — an arm64-only kintsugi-mas would leave
# every Intel Mac's App Store rows failing with "not installed". Bumping MAS_VERSION means
# re-pinning both digests from the release's asset list (the GitHub API reports them as
# `digest: sha256:...`) and re-verifying `mas update` from a LaunchDaemon, since mas drives Apple's
# private CommerceKit and has broken on macOS majors before. mas 7 needs macOS 13 or newer.
MAS_VERSION="7.0.0"
MAS_ARM64_SHA256="bc218a854c85d9e1c95496a96b26287bb66056b95688b90f91d04fa62ed21b75"
MAS_X86_64_SHA256="9bca1de1fb6ea19c9e0b9d7b7c85249020c9ed65a9d434c2ef40896a2f3395f9"

API_BASE_URL="${AGENT_API_BASE_URL:-https://kintsugi.example.com:8443}"
RELEASE_NOTES=""
PREBUILT_BINARY=""
PREBUILT_MAS=""
OUTPUT_DIR=""
# The fleet's code-signing identity, by name — minted once by
# packaging/create-signing-identity.sh, held only as a GitHub Actions secret, and imported by
# .github/workflows/ci.yml for the length of the release job. Looked up by name here so that job's
# `--signing-identity` and a hand-run publish resolve the same thing. That name is a string this
# script, install.sh, create-signing-identity.sh and ci.yml all have to agree on; nothing checks
# that they do.
SIGNING_IDENTITY_NAME="Kintsugi Agent Signing"
SIGNING_IDENTITY="${KINTSUGI_CODESIGN_IDENTITY:-}"
AD_HOC=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --api-base-url)
            [[ $# -ge 2 ]] || { echo "--api-base-url requires a value" >&2; exit 1; }
            API_BASE_URL="$2"
            shift 2
            ;;
        --release-notes)
            [[ $# -ge 2 ]] || { echo "--release-notes requires a value" >&2; exit 1; }
            RELEASE_NOTES="$2"
            shift 2
            ;;
        --binary)
            [[ $# -ge 2 ]] || { echo "--binary requires a value" >&2; exit 1; }
            PREBUILT_BINARY="$2"
            shift 2
            ;;
        --output-dir)
            [[ $# -ge 2 ]] || { echo "--output-dir requires a value" >&2; exit 1; }
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --mas-binary)
            [[ $# -ge 2 ]] || { echo "--mas-binary requires a value" >&2; exit 1; }
            PREBUILT_MAS="$2"
            shift 2
            ;;
        --signing-identity)
            [[ $# -ge 2 ]] || { echo "--signing-identity requires a value" >&2; exit 1; }
            SIGNING_IDENTITY="$2"
            shift 2
            ;;
        --ad-hoc)
            # For a throwaway package that is never going near a real host. It says so out loud
            # below, because a package built this way orphans every TCC grant on whatever installs
            # it, and does so unattended.
            AD_HOC="yes"
            shift
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

# An explicit --ad-hoc wins; then --signing-identity/$KINTSUGI_CODESIGN_IDENTITY, which is what the
# release job passes; then the identity by name if this machine happens to hold it at all. It
# normally does not, and that is the design rather than a gap — see the note on custody above.
if [[ -n "$AD_HOC" ]]; then
    SIGNING_IDENTITY=""
elif [[ -z "$SIGNING_IDENTITY" ]] \
        && security find-identity -v -p codesigning 2>/dev/null | grep -qF "$SIGNING_IDENTITY_NAME"; then
    SIGNING_IDENTITY="$SIGNING_IDENTITY_NAME"
fi

if [[ -z "$SIGNING_IDENTITY" ]]; then
    echo "WARNING: no code-signing identity here; signing ad hoc." >&2
    echo "  Every host installing this package must re-grant Screen Recording and Accessibility by" >&2
    echo "  hand, and will have to again on its next self-update: an ad-hoc designated requirement" >&2
    echo "  is a hash of this exact build, so no later build ever satisfies the grant TCC recorded." >&2
    if [[ -z "$AD_HOC" ]]; then
        echo "  The fleet identity lives in the repository's Actions secrets, so the build to hand a" >&2
        echo "  fleet is the GitHub release (Clients screen > Refresh clients). Pass --ad-hoc to say" >&2
        echo "  you meant this one." >&2
    fi
fi

# Signs one file and then *asserts* what the signature actually says, in the same spirit as the
# `lipo -archs` check below: the point of a real identity is a designated requirement that names
# the certificate rather than this build's hash, and a signature that quietly came out cdhash-only
# would be a fleet-wide grant wipe with nothing in any log naming the cause.
#
# --timestamp=none is stated rather than left to a default: nothing here is notarized, so a
# timestamp buys nothing, and a signing step that reaches a timestamp authority is one that can fail
# a release for reasons that have nothing to do with the build. The identifier is fixed rather than
# derived from the file name, so it is the same whether the input came from CI's lipo output or a
# local target/release build.
#
# install.sh signs the same way, for the same reasons, in its build-from-source path — which is the
# path a developer's `cargo build --release && sudo ./install.sh` loop takes, and it never comes
# near this script.
sign_file() {
    local path="$1" identifier="$2" requirement
    if [[ -n "$SIGNING_IDENTITY" ]]; then
        codesign --sign "$SIGNING_IDENTITY" --force --identifier "$identifier" --timestamp=none "$path"
    else
        codesign --sign - --force --identifier "$identifier" "$path"
    fi
    codesign --verify --strict "$path"

    requirement="$(codesign --display --requirements - "$path" 2>&1)"
    echo "$requirement"
    # The property being asserted is the absence of `cdhash`, not the presence of any particular
    # spelling: a self-signed leaf comes out as `certificate leaf = H"..."`, an Apple-anchored one
    # names `anchor apple generic` as well, and betting on either wording would fail a release for
    # a signature that was perfectly good. A cdhash clause is the one thing that must not be there
    # — that is the per-build requirement whose whole problem is that no later build satisfies it.
    # The certificate/anchor test beside it only catches output that named nothing at all.
    if [[ -n "$SIGNING_IDENTITY" ]]; then
        if grep -q 'cdhash' <<<"$requirement" \
                || ! grep -qE 'certificate|anchor' <<<"$requirement"; then
            echo "Refusing to publish: $(basename "$path") was signed with '$SIGNING_IDENTITY' but its" >&2
            echo "designated requirement does not name that certificate. A per-build requirement" >&2
            echo "orphans every host's Screen Recording and Accessibility grant on install." >&2
            exit 1
        fi
    fi
}

VERSION="$(grep -m1 '^version' "$PROJECT_DIR/Cargo.toml" | sed -E 's/version *= *"([^"]+)"/\1/')"
if [[ -z "$VERSION" ]]; then
    echo "Could not read the version from $PROJECT_DIR/Cargo.toml" >&2
    exit 1
fi

if [[ -n "$PREBUILT_BINARY" ]]; then
    echo "Packaging kintsugi-agent v${VERSION} from ${PREBUILT_BINARY}..."
    BUILT_BIN="$PREBUILT_BINARY"
else
    echo "Building kintsugi-agent v${VERSION} (release)..."
    (cd "$PROJECT_DIR" && cargo build --release)
    BUILT_BIN="$PROJECT_DIR/target/release/kintsugi-agent"
fi
[[ -f "$BUILT_BIN" ]] || { echo "Expected build output not found at $BUILT_BIN" >&2; exit 1; }

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

cp "$BUILT_BIN" "$WORK_DIR/kintsugi-agent"

# Signed here, on the copy, so every slice of whatever was built carries one signature over the
# whole file. `cargo build` alone does not give one: the linker signs the arm64 slice (macOS will
# not exec an unsigned arm64 image) but as "linker-signed" — no designated requirement — and leaves
# an x86_64 slice unsigned altogether. TCC accepts neither as an identity. System Settings records a
# Screen Recording or Accessibility grant against a requirement it synthesises, the running process
# never satisfies it, and remote control reports both permissions missing however many times the
# administrator re-adds the binary — which is how 0.6.0 shipped.
#
# It used to be signed *ad hoc*, which fixed that only until the next release: an ad-hoc designated
# requirement is `cdhash H"..."` per slice, a hash of that exact build, so `self_update` replacing
# the binary unattended wiped both grants across the fleet on every release. The fleet identity
# above makes the requirement name the certificate instead, and every future build satisfies it.
sign_file "$WORK_DIR/kintsugi-agent" kintsugi-agent
cp "$SCRIPT_DIR/config.toml" "$WORK_DIR/config.toml"
cp "$SCRIPT_DIR/au.com.sharpblue.kintsugiagent.plist" "$WORK_DIR/au.com.sharpblue.kintsugiagent.plist"
cp "$SCRIPT_DIR/au.com.sharpblue.kintsugiagent-ui.plist" "$WORK_DIR/au.com.sharpblue.kintsugiagent-ui.plist"
# The remote-shell job. It has to travel in the archive as well as be installed by install.sh,
# because self_update installs it on a host that has not got one — see
# self_update::install_remote_shell_job_if_absent, without which a Mac updating from a release that
# predates remote shells would report every terminal session as never connecting.
cp "$SCRIPT_DIR/au.com.sharpblue.kintsugiagent-remote-shell.plist" "$WORK_DIR/au.com.sharpblue.kintsugiagent-remote-shell.plist"
cp "$SCRIPT_DIR/install.sh" "$WORK_DIR/install.sh"
cp "$SCRIPT_DIR/uninstall.sh" "$WORK_DIR/uninstall.sh"

# Extracts the Mach-O out of one of mas's per-architecture installer packages. The .pkg installs a
# zsh wrapper at bin/mas that formats tabular output (and wants jq for it); the real program is
# libexec/bin/mas, and `mas update` needs nothing from the wrapper.
fetch_mas_slice() {
    local arch="$1" expected_sha="$2" dest="$3"
    local pkg="$WORK_DIR/mas-${arch}.pkg" expanded="$WORK_DIR/mas-${arch}-expanded"
    curl -fsSL -o "$pkg" "https://github.com/mas-cli/mas/releases/download/v${MAS_VERSION}/mas-${MAS_VERSION}-${arch}.pkg"
    local actual_sha
    actual_sha="$(shasum -a 256 "$pkg" | cut -d' ' -f1)"
    if [[ "$actual_sha" != "$expected_sha" ]]; then
        echo "mas-${MAS_VERSION}-${arch}.pkg digest mismatch: expected ${expected_sha}, got ${actual_sha}" >&2
        echo "Refusing to package a mas binary that is not the one this script was pinned against." >&2
        exit 1
    fi
    pkgutil --expand-full "$pkg" "$expanded"
    cp "$expanded/mas.pkg/Payload/usr/local/opt/mas/libexec/bin/mas" "$dest"
}

if [[ -n "$PREBUILT_MAS" ]]; then
    echo "Packaging kintsugi-mas from ${PREBUILT_MAS}..."
    cp "$PREBUILT_MAS" "$WORK_DIR/kintsugi-mas"
else
    echo "Fetching mas v${MAS_VERSION} (arm64 + x86_64) and building a universal kintsugi-mas..."
    fetch_mas_slice arm64 "$MAS_ARM64_SHA256" "$WORK_DIR/mas-arm64"
    fetch_mas_slice x86_64 "$MAS_X86_64_SHA256" "$WORK_DIR/mas-x86_64"
    lipo -create -output "$WORK_DIR/kintsugi-mas" "$WORK_DIR/mas-arm64" "$WORK_DIR/mas-x86_64"
fi
# Asserted rather than trusted: a single-architecture kintsugi-mas is exactly the artifact that
# would pass every test on the build host and fail on half the fleet.
if ! lipo -archs "$WORK_DIR/kintsugi-mas" | grep -q 'x86_64' || ! lipo -archs "$WORK_DIR/kintsugi-mas" | grep -q 'arm64'; then
    echo "kintsugi-mas is not universal: $(lipo -archs "$WORK_DIR/kintsugi-mas")" >&2
    exit 1
fi
# Signed like the agent, for the same reason: lipo keeps each slice's original signature, but one
# identity over the whole file is what a fleet CodeRequirement names. mas carries no entitlements,
# so re-signing loses nothing. It needs no TCC grant of its own — it is signed with the fleet
# identity to be the one kind of artifact this archive ships, rather than two.
sign_file "$WORK_DIR/kintsugi-mas" kintsugi-mas
lipo -info "$WORK_DIR/kintsugi-mas"

ARCHIVE_NAME="kintsugi-agent-macos-${VERSION}.tar.gz"
ARCHIVE_PATH="$WORK_DIR/$ARCHIVE_NAME"
# -C + bare filenames, not full source paths, so the archive's top-level entries are exactly
# "kintsugi-agent", "install.sh", etc. — what both install.sh's own instructions and
# self_update.rs's extraction expect, rather than being nested under a temp-dir path.
tar -czf "$ARCHIVE_PATH" -C "$WORK_DIR" \
    kintsugi-agent kintsugi-mas config.toml \
    au.com.sharpblue.kintsugiagent.plist au.com.sharpblue.kintsugiagent-ui.plist \
    au.com.sharpblue.kintsugiagent-remote-shell.plist \
    install.sh uninstall.sh

# --output-dir stops here: the archive is the deliverable, and there is no server to send it to.
# The trap above wipes WORK_DIR on exit, so it has to be copied out before that happens.
if [[ -n "$OUTPUT_DIR" ]]; then
    mkdir -p "$OUTPUT_DIR"
    cp "$ARCHIVE_PATH" "$OUTPUT_DIR/$ARCHIVE_NAME"
    echo "Wrote ${OUTPUT_DIR%/}/${ARCHIVE_NAME}"
    exit 0
fi

echo "Publishing ${ARCHIVE_NAME} to ${API_BASE_URL}..."
RESPONSE="$(curl -sS -w '\n%{http_code}' \
    -F "platform=macos" \
    -F "version=${VERSION}" \
    -F "releaseNotes=${RELEASE_NOTES}" \
    -F "file=@${ARCHIVE_PATH};filename=${ARCHIVE_NAME}" \
    "${API_BASE_URL%/}/api/agent-packages")"
HTTP_STATUS="$(echo "$RESPONSE" | tail -1)"
BODY="$(echo "$RESPONSE" | sed '$d')"

if [[ "$HTTP_STATUS" != "200" ]]; then
    echo "Publish failed (HTTP ${HTTP_STATUS}): ${BODY}" >&2
    exit 1
fi

echo "Published: ${BODY}"
