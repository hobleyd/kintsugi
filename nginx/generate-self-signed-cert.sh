#!/usr/bin/env bash
#
# Writes a self-signed certificate into nginx/tls/ so the stack can be brought up for local
# testing without a real one.
#
# ---------------------------------------------------------------------------------------------
# READ THIS BEFORE USING IT ON ANYTHING THAT HAS AGENTS
# ---------------------------------------------------------------------------------------------
# nginx's server certificate is what every agent validates, and it validates through
# rustls-tls-native-roots — against the *host OS* trust store, with no way to pin or except
# anything. A self-signed certificate here is therefore rejected during the TLS handshake, and the
# entire fleet stops checking in at once. Nothing degrades gracefully; every agent simply goes
# quiet, and the Hosts screen shows last-seen times marching into the past.
#
# So this is for a machine with no agents pointed at it. For anything else, the certificate must
# hold a publicly-trusted chain, complete down to a root the agents' platforms actually ship — see
# the note in CLAUDE.md about a truncated fullchain.pem, which curl and browsers forgive and rustls
# does not.
#
# Two alternatives that do not have this problem, in order of preference:
#
#   1. If you already hold a real certificate for a domain you control, add a hosts entry pointing
#      one of its names at 127.0.0.1 and browse that name instead of localhost. The certificate is
#      genuinely valid for it, the browser shows no warning, and agents keep working.
#   2. mkcert (https://github.com/FiloSottile/mkcert) installs a local CA into the OS trust store,
#      so certificates it issues are trusted by browsers *and* by rustls on that machine. Better
#      than this script if the agents you are testing run on the same host.
#
# Usage:
#   nginx/generate-self-signed-cert.sh [name ...]
#
#   Names may be hostnames or IP addresses and all become subject alternative names; browsers have
#   ignored the common name for years, so a certificate without a matching SAN is rejected outright
#   rather than merely warned about. Defaults to localhost, 127.0.0.1 and ::1.
#
#   Set FORCE=1 to overwrite an existing certificate. Without it this script refuses, because the
#   thing it would overwrite is quite possibly the real one.
set -euo pipefail

script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
tls_directory="$script_directory/tls"
certificate_path="$tls_directory/fullchain.pem"
key_path="$tls_directory/privkey.pem"

names=("$@")
if [ ${#names[@]} -eq 0 ]; then
    names=(localhost 127.0.0.1 ::1)
fi

if [ -e "$certificate_path" ] && [ "${FORCE:-0}" != "1" ]; then
    echo "error: $certificate_path already exists." >&2
    echo >&2
    # Said at this length because overwriting a real certificate with a self-signed one is exactly
    # how a whole fleet goes dark, and the mistake is one keystroke away.
    if subject=$(openssl x509 -in "$certificate_path" -noout -subject 2>/dev/null); then
        echo "It is currently: $subject" >&2
        openssl x509 -in "$certificate_path" -noout -issuer -dates 2>/dev/null | sed 's/^/  /' >&2
        echo >&2
        echo "If that is a real certificate, do not replace it: every agent validates this file" >&2
        echo "against the host OS trust store, and a self-signed one is refused at the handshake." >&2
        echo "To reach it locally, add a hosts entry for one of the names it already covers:" >&2
        openssl x509 -in "$certificate_path" -noout -ext subjectAltName 2>/dev/null | sed 's/^/  /' >&2
    fi
    echo >&2
    echo "Set FORCE=1 to overwrite it anyway." >&2
    exit 1
fi

mkdir -p "$tls_directory"

# Built as a config file rather than passed with -addext, because the SAN list is variable-length
# and this is also the only way to get the extensions onto a self-signed certificate in one pass
# on the openssl versions shipped by both macOS (LibreSSL) and Debian.
config=$(mktemp)
trap 'rm -f "$config"' EXIT

{
    echo '[req]'
    echo 'distinguished_name = subject'
    echo 'x509_extensions = extensions'
    echo 'prompt = no'
    echo
    echo '[subject]'
    # Named for what it is, so it is obvious in a browser's certificate viewer which certificate
    # is being looked at and why it is not trusted.
    echo "CN = Kintsugi local testing (self-signed)"
    echo
    echo '[extensions]'
    echo 'basicConstraints = critical, CA:FALSE'
    echo 'keyUsage = critical, digitalSignature, keyEncipherment'
    echo 'extendedKeyUsage = serverAuth'
    echo 'subjectAltName = @names'
    echo
    echo '[names]'

    dns_index=1
    ip_index=1
    for name in "${names[@]}"; do
        # An IP literal has to be an IP-type SAN, not a DNS one: a DNS SAN of "127.0.0.1" matches
        # nothing, and the failure looks identical to having no SAN at all.
        if [[ "$name" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || [[ "$name" == *:* ]]; then
            echo "IP.$ip_index = $name"
            ip_index=$((ip_index + 1))
        else
            echo "DNS.$dns_index = $name"
            dns_index=$((dns_index + 1))
        fi
    done
} > "$config"

# 825 days is the maximum lifetime browsers accept for a publicly-trusted certificate; there is no
# such rule for one you trust by hand, but staying inside it means nothing rejects this on duration
# alone. P-256 to match what the fleet CA uses.
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
    -keyout "$key_path" -out "$certificate_path" \
    -days 825 -sha256 -config "$config" >/dev/null 2>&1

chmod 600 "$key_path"
chmod 644 "$certificate_path"

echo "Wrote a self-signed certificate for local testing:"
echo "  $certificate_path"
echo "  $key_path"
echo
openssl x509 -in "$certificate_path" -noout -subject -dates -ext subjectAltName | sed 's/^/  /'
echo
echo "Restart nginx to pick it up:"
echo "  docker compose restart nginx"
echo
echo "Your browser will warn about it; that is the expected and only symptom on a machine with no"
echo "agents. If agents do point here, they will stop checking in entirely — see the header of"
echo "this script."
