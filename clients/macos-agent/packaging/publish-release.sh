#!/bin/bash
# Builds the release binary, bundles it with everything a brand-new install needs (config.toml,
# both LaunchDaemon/LaunchAgent plists, install.sh, uninstall.sh — the same set dist/ has always
# held as kintsugi-agent-macos-installer.tar.gz), and publishes that one bundle to the server.
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
#   packaging/publish-release.sh --release-notes "Fixes the menu bar version label"
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
# .github/workflows/release-clients.yml.
set -euo pipefail

API_BASE_URL="${AGENT_API_BASE_URL:-https://kintsugi.example.com:8443}"
RELEASE_NOTES=""
PREBUILT_BINARY=""
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

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

cp "$BUILT_BIN" "$WORK_DIR/kintsugi-agent"
cp "$SCRIPT_DIR/config.toml" "$WORK_DIR/config.toml"
cp "$SCRIPT_DIR/au.com.sharpblue.kintsugiagent.plist" "$WORK_DIR/au.com.sharpblue.kintsugiagent.plist"
cp "$SCRIPT_DIR/au.com.sharpblue.kintsugiagent-ui.plist" "$WORK_DIR/au.com.sharpblue.kintsugiagent-ui.plist"
cp "$SCRIPT_DIR/install.sh" "$WORK_DIR/install.sh"
cp "$SCRIPT_DIR/uninstall.sh" "$WORK_DIR/uninstall.sh"

ARCHIVE_NAME="kintsugi-agent-macos-${VERSION}.tar.gz"
ARCHIVE_PATH="$WORK_DIR/$ARCHIVE_NAME"
# -C + bare filenames, not full source paths, so the archive's top-level entries are exactly
# "kintsugi-agent", "install.sh", etc. — what both install.sh's own instructions and
# self_update.rs's extraction expect, rather than being nested under a temp-dir path.
tar -czf "$ARCHIVE_PATH" -C "$WORK_DIR" \
    kintsugi-agent config.toml \
    au.com.sharpblue.kintsugiagent.plist au.com.sharpblue.kintsugiagent-ui.plist \
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
