#!/bin/bash
# Mints the ONE code-signing identity every released kintsugi-agent build is signed with, and puts
# its private key where the only thing that needs it lives: this repository's GitHub Actions
# secrets. Run it once, ever — not once per machine, and not once per release. The whole point is
# that the identity outlives every build.
#
# WHY THIS EXISTS
# ---------------
# Ad-hoc signing (`codesign --sign -`) gives macOS no signing identity, so the binary's designated
# requirement is a bare `cdhash` — a hash of that exact build, one per architecture slice:
#
#     designated => cdhash H"fbb593..." or cdhash H"105d13..."
#
# TCC records that requirement when somebody grants Screen Recording or Accessibility, and
# re-checks the running process against it on every access. A new build hashes differently, so it
# satisfies nothing: both grants are silently orphaned, System Settings still shows the binary
# switched on, and remote control reports both permissions missing. Because `self_update` replaces
# the binary unattended, every release wiped those grants across the whole fleet.
#
# Signing with a stable certificate makes the requirement name the *identity* instead:
#
#     designated => identifier "kintsugi-agent" and certificate leaf = H"<one hash, forever>"
#
# Every future build signed with this certificate satisfies it, so the grants survive releases and
# self-updates. TCC keys its row on the binary's path as well as that requirement, so it is the
# installed /usr/local/bin/kintsugi-agent that keeps its grants.
#
# WHERE THE KEY LIVES, AND WHY IT IS NOT HERE
# -------------------------------------------
# The agent that gets deployed is built by CI (.github/workflows/ci.yml's release-macos job, which
# is the only thing that produces the universal binary a host installs and self-updates to), so CI
# is the only thing that has to be able to sign. --set-secrets therefore pushes the PKCS#12 and its
# password straight into the repository's Actions secrets and keeps no local copy: nothing on this
# machine holds the fleet private key, and there is no second identity to drift out of step with
# the one hosts have already granted.
#
# GUARD IT. Anything signed with this key satisfies the fleet's code requirement, which is what the
# PPPC profile in kintsugi-remote-control.mobileconfig.example pre-approves for Screen Recording
# and Accessibility. It is a fleet credential, not a build convenience — which is the other half of
# why it is a repository secret rather than a file on a laptop.
#
#   packaging/create-signing-identity.sh --set-secrets
#   packaging/create-signing-identity.sh --set-secrets --repo owner/repo
#   packaging/create-signing-identity.sh --export-p12 ~/kintsugi-signing.p12   # load them by hand
#
# There is nothing to verify locally: the identity is exercised for the first time by the release
# job, which imports it, asserts codesign will use it, and refuses to publish a package whose
# designated requirement still names a hash (see publish-release.sh's sign_file). A local
# `cargo build` + install.sh signs ad hoc, and says so.
#
# ONE LAST RE-GRANT. Moving from ad-hoc to this identity changes the requirement, so every Mac
# already carrying grants needs both rows removed and re-added one final time (and the per-user
# process relaunched: launchctl kickstart -k gui/$(id -u)/au.com.sharpblue.kintsugiagent-ui). After
# that it should not need doing again.
#
# LOSING IT costs the fleet another one of those, since a fresh identity is a fresh requirement.
# There is deliberately no backup: a copy kept anywhere is a copy of the fleet's signing key. If it
# has to be replaced, replace both secrets and expect the re-grant.
set -euo pipefail

# The identity's name, and the string four other places have to agree on: the PKCS#12 carries it as
# its friendly name and its CN, ci.yml looks the certificate up by it after importing, and
# publish-release.sh and install.sh find the identity by it. CLAUDE.md's couplings list is the
# fifth. Nothing checks that they agree.
NAME="Kintsugi Agent Signing"

# The two secrets .github/workflows/ci.yml reads. Renaming either means renaming it there too.
P12_SECRET="MACOS_SIGNING_CERTIFICATE_P12"
PASSWORD_SECRET="MACOS_SIGNING_CERTIFICATE_PASSWORD"

SET_SECRETS=""
REPO=""
EXPORT_P12=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --set-secrets)
            SET_SECRETS="yes"
            shift
            ;;
        --repo)
            [[ $# -ge 2 ]] || { echo "--repo requires owner/repo" >&2; exit 1; }
            REPO="$2"
            shift 2
            ;;
        --export-p12)
            [[ $# -ge 2 ]] || { echo "--export-p12 requires a path" >&2; exit 1; }
            EXPORT_P12="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

# No default destination on purpose. A private key written to a path nobody named is a private key
# that ends up somewhere it should not be, and this one is a fleet credential.
if [[ -z "$SET_SECRETS" && -z "$EXPORT_P12" ]]; then
    cat >&2 <<EOF
Say where the identity is to go — it is generated here and is not kept here:

    packaging/create-signing-identity.sh --set-secrets            # straight into Actions secrets
    packaging/create-signing-identity.sh --export-p12 <path>      # write it out and load it by hand

--set-secrets needs the gh CLI, authenticated with write access to this repository's secrets, and
is the form to prefer: the key reaches ${P12_SECRET} without ever being a file
somebody has to remember to delete.
EOF
    exit 1
fi

command -v openssl >/dev/null || { echo "openssl is required and was not found on PATH." >&2; exit 1; }
if [[ -n "$SET_SECRETS" ]]; then
    command -v gh >/dev/null || { echo "--set-secrets needs the gh CLI (https://cli.github.com)." >&2; exit 1; }
    # Checked before a key exists rather than after: an authentication failure at the upload step
    # would leave a generated identity with nowhere to go and no copy of it.
    gh auth status >/dev/null 2>&1 || { echo "gh is not authenticated: run 'gh auth login' first." >&2; exit 1; }
fi

# 0700, because the PKCS#12 written inside it holds the private key.
WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT

echo "==> generating a key and a self-signed code-signing certificate (10 years)"
# codeSigning EKU marked critical, and CA:false: without the EKU the certificate exists but
# `security find-identity -p codesigning` will not offer it in the release job, and codesign then
# cannot see it.
openssl req -x509 -newkey rsa:2048 -keyout "$WORK/key.pem" -out "$WORK/cert.pem" -days 3650 -nodes \
    -subj "/CN=$NAME/O=Kintsugi" \
    -addext "extendedKeyUsage=critical,codeSigning" \
    -addext "basicConstraints=critical,CA:false" \
    -addext "keyUsage=critical,digitalSignature" 2>/dev/null

# OpenSSL 3 defaults to PKCS#12 encryption that macOS's importer rejects with "MAC verification
# failed", and -legacy is what turns that off — so the release job's `security import` would fail
# on a bundle written without it. LibreSSL, which is what /usr/bin/openssl is on a stock macOS,
# already writes the legacy form and has no such flag, so the option is passed only to an OpenSSL 3
# or newer that understands it. An empty password fails that import the same way a modern cipher
# does, so there is a generated one either way.
# Expanded below as ${LEGACY[@]+"${LEGACY[@]}"} rather than "${LEGACY[@]}": macOS ships bash 3.2,
# where an empty array expanded under `set -u` is an unbound variable and aborts the script.
LEGACY=()
if openssl version 2>/dev/null | grep -qE '^OpenSSL [3-9]'; then
    LEGACY=(-legacy)
fi
P12_PASSWORD="$(uuidgen)"

echo "==> packaging it as PKCS#12"
openssl pkcs12 -export -out "$WORK/identity.p12" -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -passout "pass:$P12_PASSWORD" -name "$NAME" ${LEGACY[@]+"${LEGACY[@]}"} 2>/dev/null

# Base64 because a GitHub secret is text and a PKCS#12 is not. ci.yml decodes it with
# `base64 --decode`; -A keeps it on one line, which is what a secret's value has to be.
openssl base64 -A -in "$WORK/identity.p12" -out "$WORK/identity.p12.base64"

if [[ -n "$SET_SECRETS" ]]; then
    GH_REPO_ARGS=()
    [[ -n "$REPO" ]] && GH_REPO_ARGS=(--repo "$REPO")

    echo "==> setting ${P12_SECRET} and ${PASSWORD_SECRET}"
    # Fed on stdin, never as an argument: a value on a command line is visible in this machine's
    # process list for as long as the call takes.
    gh secret set "$P12_SECRET" ${GH_REPO_ARGS[@]+"${GH_REPO_ARGS[@]}"} < "$WORK/identity.p12.base64"
    printf '%s' "$P12_PASSWORD" | gh secret set "$PASSWORD_SECRET" ${GH_REPO_ARGS[@]+"${GH_REPO_ARGS[@]}"}

    cat <<EOF

Done. Both secrets are set and nothing here holds the key — this directory is wiped on exit.

The next macOS agent release signs with it. Nothing needs doing on this machine, and nothing needs
doing per release; the release job (.github/workflows/ci.yml) imports the identity, checks codesign
will use it, and publish-release.sh refuses to publish a package whose designated requirement still
names a build hash.

One thing left, once: every Mac that already carries grants needs Screen Recording and
Accessibility removed and re-added for /usr/local/bin/kintsugi-agent after it self-updates to that
release, because the requirement has changed — then relaunch the per-user process:

    launchctl kickstart -k gui/\$(id -u)/au.com.sharpblue.kintsugiagent-ui

That is the last time it should be necessary.
EOF
fi

if [[ -n "$EXPORT_P12" ]]; then
    # Created empty at 0600 first, so the key is never briefly readable by anyone else.
    install -m 600 /dev/null "$EXPORT_P12"
    cat "$WORK/identity.p12" > "$EXPORT_P12"
    cat <<EOF

==> wrote $EXPORT_P12 (mode 600)

Load it into the two secrets .github/workflows/ci.yml reads, then delete the file — CI is the only
thing that needs to sign, and a copy left on this machine is a copy of the fleet's signing key:

    ${P12_SECRET}        = base64 of that file (\`openssl base64 -A -in <file>\`)
    ${PASSWORD_SECRET}   = $P12_PASSWORD

    rm -P "$EXPORT_P12"
EOF
fi
