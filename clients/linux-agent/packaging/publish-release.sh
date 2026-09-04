#!/bin/bash
# Builds the release binary, bundles it with everything a brand-new install needs (config.toml,
# the systemd units, install.sh, uninstall.sh), and publishes that one bundle to the server.
#
# It does double duty: a human downloads it from the Clients page for a fresh install, and an
# already-enrolled agent's own auto-update check downloads the very same file and just extracts
# the "kintsugi-agent" entry out of it, ignoring the rest (see self_update.rs's extraction) — so
# there's only ever one artifact to build and publish, not two.
#
# The bundled config.toml's enrollment_token is left blank here on purpose: the server rewrites it
# to whatever AGENT_ENROLLMENT_TOKEN currently is on every download request, not just once at
# publish time — see AgentPackageArchiveRewriter — so a token rotation never makes an already-
# published package stale, and there's no --enrollment-token flag to remember to pass here.
#
#   packaging/publish-release.sh
#   packaging/publish-release.sh --api-base-url https://kintsugi.example.com:8443
#   packaging/publish-release.sh --release-notes "Fixes the snap list parser"
#
# The version published is always this crate's own Cargo.toml version — bump that first. Run from
# a plain (non-root) shell; unlike install.sh this never needs sudo, since it's talking to the
# server over the network, not touching this machine's systemd/filesystem.
#
# Build host note: the published binary must run on every distribution in the fleet, and a glibc
# binary only runs on a glibc at least as new as the one it was linked against. Build on the
# *oldest* distribution you support (or in a container of it) — this script does not pick a target
# for you.
#
# That note applies with full force to kintsugi-agent-wayland, and *only* to it once CI is doing the
# building: the agent itself is statically linked against musl there, so it has no libc floor at all,
# while the Wayland backend links libpipewire and therefore glibc and cannot. It is the one binary in
# this fleet with a floor. The consequence is confined rather than fatal — a host whose glibc is too
# old fails to exec it, the agent reports Wayland capture as unavailable, and everything else keeps
# working — but it is the reason CI builds that one on the oldest distribution it can.
#
# CI builds the binary itself — a universal one on macOS, a static musl one on Linux, neither of
# which a plain `cargo build --release` on the build host produces — and has no route to anyone's
# server. So both halves of this script are separable: --binary packages an already-built binary
# instead of building one, and --output-dir writes the tarball to a directory and stops before
# publishing. The tar invocation below stays the single owner of the archive's top-level entry
# names either way, because those names are what self_update.rs extracts by — reimplementing the
# `tar` call in a workflow file would let the two drift apart silently. See
# .github/workflows/release-clients.yml.
set -euo pipefail

API_BASE_URL="${AGENT_API_BASE_URL:-https://kintsugi.example.com:8443}"
RELEASE_NOTES=""
PREBUILT_BINARY=""
PREBUILT_WAYLAND_BINARY=""
OUTPUT_DIR=""
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
        --wayland-binary)
            [[ $# -ge 2 ]] || { echo "--wayland-binary requires a value" >&2; exit 1; }
            PREBUILT_WAYLAND_BINARY="$2"
            shift 2
            ;;
        --output-dir)
            [[ $# -ge 2 ]] || { echo "--output-dir requires a value" >&2; exit 1; }
            OUTPUT_DIR="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"

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

# The Wayland backend, which is a *separate binary and deliberately optional*.
#
# It links libpipewire, so unlike the agent it cannot be a static musl build and it needs the
# PipeWire development package to compile — which is why this does not fail when it is missing. An
# archive without it installs and runs exactly as before; the agent reports Wayland hosts as
# unreachable with a sentence saying the backend is not installed, and X11 hosts are unaffected.
# Publishing a silently Wayland-less package is a real cost, so it says so loudly.
WAYLAND_DIR="$(dirname "$PROJECT_DIR")/linux-agent-wayland"
if [[ -n "$PREBUILT_WAYLAND_BINARY" ]]; then
    BUILT_WAYLAND_BIN="$PREBUILT_WAYLAND_BINARY"
    [[ -f "$BUILT_WAYLAND_BIN" ]] || { echo "No Wayland backend at $BUILT_WAYLAND_BIN" >&2; exit 1; }
elif [[ -d "$WAYLAND_DIR" ]] && pkg-config --exists libpipewire-0.3 2>/dev/null; then
    echo "Building kintsugi-agent-wayland (release)..."
    (cd "$WAYLAND_DIR" && cargo build --release)
    BUILT_WAYLAND_BIN="$WAYLAND_DIR/target/release/kintsugi-agent-wayland"
else
    BUILT_WAYLAND_BIN=""
    echo "WARNING: no PipeWire development package here, so the Wayland backend is not being" >&2
    echo "         built or packaged. Hosts running a Wayland session will report remote control" >&2
    echo "         as unavailable. Install libpipewire-0.3-dev (or pass --wayland-binary)." >&2
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

cp "$BUILT_BIN" "$WORK_DIR/kintsugi-agent"
ARCHIVE_ENTRIES=(kintsugi-agent)
if [[ -n "$BUILT_WAYLAND_BIN" ]]; then
    cp "$BUILT_WAYLAND_BIN" "$WORK_DIR/kintsugi-agent-wayland"
    ARCHIVE_ENTRIES+=(kintsugi-agent-wayland)
fi
cp "$SCRIPT_DIR/config.toml" "$WORK_DIR/config.toml"
cp "$SCRIPT_DIR/kintsugi-agent.service" "$WORK_DIR/kintsugi-agent.service"
cp "$SCRIPT_DIR/kintsugi-agent.timer" "$WORK_DIR/kintsugi-agent.timer"
cp "$SCRIPT_DIR/kintsugi-agent-queue.service" "$WORK_DIR/kintsugi-agent-queue.service"
cp "$SCRIPT_DIR/kintsugi-agent-queue.path" "$WORK_DIR/kintsugi-agent-queue.path"
cp "$SCRIPT_DIR/kintsugi-agent-ui.service" "$WORK_DIR/kintsugi-agent-ui.service"
cp "$SCRIPT_DIR/kintsugi-agent-remote.service" "$WORK_DIR/kintsugi-agent-remote.service"
cp "$SCRIPT_DIR/install.sh" "$WORK_DIR/install.sh"
cp "$SCRIPT_DIR/uninstall.sh" "$WORK_DIR/uninstall.sh"
chmod 755 "$WORK_DIR/install.sh" "$WORK_DIR/uninstall.sh"

ARCHIVE_NAME="kintsugi-agent-linux-${VERSION}.tar.gz"
ARCHIVE_PATH="$WORK_DIR/$ARCHIVE_NAME"
# -C + bare filenames, not full source paths, so the archive's top-level entries are exactly
# "kintsugi-agent", "install.sh", etc. — what both install.sh's own instructions and
# self_update.rs's extraction expect, rather than being nested under a temp-dir path.
tar -czf "$ARCHIVE_PATH" -C "$WORK_DIR" \
    "${ARCHIVE_ENTRIES[@]}" config.toml \
    kintsugi-agent.service kintsugi-agent.timer \
    kintsugi-agent-queue.service kintsugi-agent-queue.path \
    kintsugi-agent-ui.service kintsugi-agent-remote.service \
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
    -F "platform=linux" \
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
