use std::fs;
use std::path::Path;

use anyhow::{Context, Result};
use base64::Engine;
use p256::ecdsa::signature::Verifier;
use p256::ecdsa::{Signature, VerifyingKey};
use p256::pkcs8::DecodePublicKey;
use serde::{Deserialize, Serialize};

use crate::config::Config;

const CERT_FILE: &str = "agent.crt";
const KEY_FILE: &str = "agent.key";
const CA_FILE: &str = "ca.crt";
const ARTIFACT_PUBKEY_FILE: &str = "artifact-signing.pub";

/// This agent's mutual-TLS identity, established once via `enroll` (see
/// Kintsugi.Application/Hosts/Commands/EnrollAgent) and reused from disk on every run after
/// that — see `load`.
#[derive(Debug, Clone)]
pub struct AgentIdentity {
    pub certificate_pem: String,
    pub private_key_pem: String,
    pub artifact_signing_public_key_pem: String,
}

#[derive(Debug, Serialize)]
struct EnrollRequest {
    #[serde(rename = "serialNumber")]
    serial_number: String,
    #[serde(rename = "enrollmentToken")]
    enrollment_token: String,
    #[serde(rename = "csrPem")]
    csr_pem: String,
}

#[derive(Debug, Deserialize)]
struct EnrollResponse {
    #[serde(rename = "certificatePem")]
    certificate_pem: String,
    #[serde(rename = "caCertificatePem")]
    ca_certificate_pem: String,
    #[serde(rename = "artifactSigningPublicKeyPem")]
    artifact_signing_public_key_pem: String,
}

/// Loads this agent's already-established identity from disk, if enrollment has already happened.
/// Returns `None` (not an error) when any expected file is missing, so the caller can fall through
/// to `enroll`.
pub fn load(dir: &Path) -> Option<AgentIdentity> {
    let certificate_pem = fs::read_to_string(dir.join(CERT_FILE)).ok()?;
    let private_key_pem = fs::read_to_string(dir.join(KEY_FILE)).ok()?;
    let artifact_signing_public_key_pem = fs::read_to_string(dir.join(ARTIFACT_PUBKEY_FILE)).ok()?;
    Some(AgentIdentity { certificate_pem, private_key_pem, artifact_signing_public_key_pem })
}

/// One-time bootstrap into the fleet's mutual-TLS identity system: generates a fresh keypair
/// locally — the private key never leaves this machine, only a CSR proving possession of the
/// *public* half is sent — and exchanges the configured enrollment token for a client certificate
/// signed by the server's own CA, plus the server's pinned artifact-signing public key (see
/// `verify_artifact_signature`). Every subsequent request nginx sees from this agent is
/// authenticated by the resulting certificate — see nginx/default.conf.
pub fn enroll(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, dir: &Path) -> Result<AgentIdentity> {
    let token = config
        .enrollment_token
        .clone()
        .context("no enrollment token configured (set enrollment_token in config.toml) — cannot enroll this agent")?;

    let key_pair =
        rcgen::KeyPair::generate_for(&rcgen::PKCS_ECDSA_P256_SHA256).context("failed to generate this agent's identity keypair")?;

    // The subject/extensions requested here are irrelevant — CaService always ignores whatever a
    // CSR itself asks for and stamps its own subject (the trusted `serial_number` this same
    // request already sends, authenticated by the enrollment token) onto the issued certificate.
    // Only the CSR's proven public key is used.
    let mut params = rcgen::CertificateParams::default();
    params.distinguished_name = rcgen::DistinguishedName::new();
    let csr_pem = params
        .serialize_request(&key_pair)
        .context("failed to build this agent's certificate signing request")?
        .pem()
        .context("failed to PEM-encode the certificate signing request")?;

    let request = EnrollRequest {
        serial_number: serial_number.to_string(),
        enrollment_token: token,
        csr_pem,
    };

    let response = client.post(config.enroll_url()).json(&request).send().context("enrollment request failed")?;

    if !response.status().is_success() {
        let status = response.status();
        let body = response.text().unwrap_or_default();
        anyhow::bail!("enrollment rejected (HTTP {status}): {body}");
    }

    let parsed: EnrollResponse = response.json().context("could not parse enrollment response")?;
    let private_key_pem = key_pair.serialize_pem();

    fs::create_dir_all(dir).with_context(|| format!("could not create identity directory {}", dir.display()))?;
    // Before the files below are written, not after: macOS gives a new file the *directory's*
    // group rather than the creating process's (BSD semantics, unlike Linux), so putting the
    // directory in `admin` here is what makes every file written into it readable by the per-user
    // --agent process.
    grant_admin_group_access(dir);
    fs::write(dir.join(CERT_FILE), &parsed.certificate_pem).context("failed to save agent certificate")?;
    fs::write(dir.join(KEY_FILE), &private_key_pem).context("failed to save agent private key")?;
    fs::write(dir.join(CA_FILE), &parsed.ca_certificate_pem).context("failed to save CA certificate")?;
    fs::write(dir.join(ARTIFACT_PUBKEY_FILE), &parsed.artifact_signing_public_key_pem)
        .context("failed to save artifact-signing public key")?;
    restrict_key_permissions(&dir.join(KEY_FILE));

    crate::logging::info(&format!("enrolled agent identity for serial number {serial_number}"));

    Ok(AgentIdentity {
        certificate_pem: parsed.certificate_pem,
        private_key_pem,
        artifact_signing_public_key_pem: parsed.artifact_signing_public_key_pem,
    })
}

/// Builds a `reqwest::Identity` (this agent's client certificate, presented for mutual TLS) from
/// `identity`, ready to hand to `ClientBuilder::identity`. Takes the combined-PEM-buffer form
/// (`Identity::from_pem`, parsed by rustls-pki-types) rather than the separate-cert/key form
/// (`Identity::from_pkcs8_pem`, native-tls-only) — see the Cargo.toml comment on `reqwest` for why.
pub fn to_reqwest_identity(identity: &AgentIdentity) -> Result<reqwest::Identity> {
    let combined_pem = format!("{}\n{}", identity.private_key_pem, identity.certificate_pem);
    reqwest::Identity::from_pem(combined_pem.as_bytes()).context("failed to build a TLS identity from the agent's certificate/key")
}

/// Builds the HTTP client every part of this agent actually talks to the server with: presenting
/// `identity` for mutual TLS when one is already established, or a plain (unauthenticated) client
/// when not — which is enough to reach `/api/host/enroll` (the one route that doesn't require a
/// certificate) but nothing else; nginx rejects every other agent route without one. See
/// nginx/default.conf.
pub fn build_client(timeout: std::time::Duration, identity: Option<&AgentIdentity>) -> Result<reqwest::blocking::Client> {
    let mut builder = reqwest::blocking::Client::builder().timeout(timeout);
    if let Some(identity) = identity {
        builder = builder.identity(to_reqwest_identity(identity)?);
    }
    builder.build().context("failed to build HTTP client")
}

/// Builds a rustls `ClientConfig` presenting this agent's certificate, for the one thing that
/// cannot go through `reqwest`: the remote control WebSocket.
///
/// `reqwest` does all of this internally for a `reqwest::Identity`, but it does not speak WebSocket,
/// and `tungstenite` wants a `ClientConfig` rather than a PEM buffer. Same certificate, same key,
/// same trust source (the host OS store, via rustls-native-certs, which is what
/// `rustls-tls-native-roots` gives the HTTP client) — so an agent that can check in can also open
/// this socket, and one that cannot will fail both the same way.
pub fn to_rustls_client_config(identity: &AgentIdentity) -> Result<std::sync::Arc<rustls::ClientConfig>> {
    let native = rustls_native_certs::load_native_certs();
    let mut roots = rustls::RootCertStore::empty();
    for certificate in native.certs {
        // Individually, ignoring failures: the macOS trust store contains certificates rustls
        // declines to parse, and one of those must not stop the other few hundred being loaded.
        let _ = roots.add(certificate);
    }

    if roots.is_empty() {
        anyhow::bail!("no trusted root certificates could be loaded from this Mac's own trust store");
    }

    let certificate_chain = rustls_pemfile::certs(&mut identity.certificate_pem.as_bytes())
        .collect::<Result<Vec<_>, _>>()
        .context("could not parse this agent's certificate")?;

    let private_key = rustls_pemfile::private_key(&mut identity.private_key_pem.as_bytes())
        .context("could not parse this agent's private key")?
        .context("this agent's key file contains no private key")?;

    let config = rustls::ClientConfig::builder()
        .with_root_certificates(roots)
        .with_client_auth_cert(certificate_chain, private_key)
        .context("could not build a TLS configuration from this agent's certificate and key")?;

    Ok(std::sync::Arc::new(config))
}

/// Loads this agent's identity from disk, enrolling it first if it doesn't exist yet. `probe_url`
/// is used only to build a short-lived, unauthenticated client for that one enrollment call.
pub fn load_or_enroll(config: &Config, serial_number: &str) -> Option<AgentIdentity> {
    let dir = crate::config::identity_dir();

    if let Some(identity) = load(&dir) {
        return Some(identity);
    }

    let enrollment_client = match build_client(std::time::Duration::from_secs(30), None) {
        Ok(client) => client,
        Err(err) => {
            crate::logging::error(&format!("could not build an HTTP client to enroll this agent: {err:#}"));
            return None;
        }
    };

    match enroll(&enrollment_client, config, serial_number, &dir) {
        Ok(identity) => Some(identity),
        Err(err) => {
            crate::logging::error(&format!(
                "could not enroll this agent's identity — every agent-only server request will be rejected until this succeeds: {err:#}"
            ));
            None
        }
    }
}

#[cfg(unix)]
/// Puts the identity directory in the `admin` group with 0770 — the same ownership
/// packaging/install.sh gives it on a fresh install.
///
/// Doing it here as well, rather than trusting install.sh to have done it, is what makes a
/// *re*-enrollment self-healing. Deleting this directory is the documented way to recover from a
/// regenerated fleet CA, and until this existed the recovery quietly half-worked: `create_dir_all`
/// running as root recreates the directory under root's own primary group (`wheel`), every file
/// written into it inherits `wheel`, and the per-user --agent process — which runs as the
/// logged-in administrator, and is in `admin`, not `wheel` — can no longer read the private key.
///
/// The half that still works is what makes it nasty. The root daemon is unaffected, so the host
/// goes on registering and checking in and the install looks healthy; only the per-user half dies,
/// and it dies as a 403 from nginx, because an identity it cannot load means it presents no client
/// certificate at all rather than reporting that it failed to read one.
///
/// macOS is the only agent this applies to: the Windows and Linux per-user processes hold no
/// identity and make no authenticated call (they go through the queue instead). Homebrew refusing
/// to run as root is why this one is different — see each agent's own queue module.
///
/// Best-effort, like `restrict_key_permissions`: on a correctly installed host install.sh has
/// already set this, and failing to enroll over a `chown` that only matters to the per-user half
/// would be the worse trade.
fn grant_admin_group_access(dir: &Path) {
    use std::os::unix::fs::PermissionsExt;

    if let Some(admin_gid) = admin_group_id() {
        // uid unchanged (u32::MAX is chown(2)'s "leave it alone"), because this already runs as
        // root and the owner is exactly right.
        let _ = std::os::unix::fs::chown(dir, None, Some(admin_gid));
    }

    if let Ok(metadata) = fs::metadata(dir) {
        let mut permissions = metadata.permissions();
        // Group needs execute as well as read to traverse into the directory at all; nothing
        // outside root and admin has any business here, hence no world bits.
        permissions.set_mode(0o770);
        let _ = fs::set_permissions(dir, permissions);
    }
}

/// The numeric gid of the `admin` group, looked up rather than hardcoded to 80 — the value is
/// stable across every macOS release to date, but a wrong gid here would hand this host's private
/// key to whichever group happened to hold that number.
fn admin_group_id() -> Option<u32> {
    // SAFETY: getgrnam() returns either a valid pointer into a thread-local static buffer (read
    // immediately, before any other libc call in this thread could invalidate it) or null. The
    // name is a literal with an explicit NUL, so it is a valid C string.
    unsafe {
        let group = libc::getgrnam(b"admin\0".as_ptr() as *const libc::c_char);
        if group.is_null() {
            return None;
        }
        Some((*group).gr_gid)
    }
}

fn restrict_key_permissions(path: &Path) {
    use std::os::unix::fs::PermissionsExt;
    if let Ok(metadata) = fs::metadata(path) {
        let mut permissions = metadata.permissions();
        // Owner (root, via the LaunchDaemon that enrolls) read/write, group (admin — see
        // packaging/install.sh's identical pattern for the OS-update queue directory) read-only,
        // so the non-root --agent process (running as the logged-in admin user) can still use this
        // same identity without the private key being world-readable.
        permissions.set_mode(0o640);
        let _ = fs::set_permissions(path, permissions);
    }
}

/// Verifies `signature_b64` (a base64-encoded, DER-formatted ECDSA-P256-SHA256 signature — see
/// Kintsugi.Infrastructure/Security/ArtifactSigningService.cs) over `content` against the
/// pinned artifact-signing public key. This is the gate between "the server said to run this" and
/// actually running it: content that never went through the server's real signing step — e.g. a
/// row written straight to the database, bypassing the normal save path — fails here instead of
/// being trusted.
pub fn verify_artifact_signature(identity: &AgentIdentity, content: &str, signature_b64: &str) -> Result<()> {
    let verifying_key = VerifyingKey::from_public_key_pem(&identity.artifact_signing_public_key_pem)
        .context("could not parse the pinned artifact-signing public key")?;

    let signature_bytes = base64::engine::general_purpose::STANDARD
        .decode(signature_b64)
        .context("signature is not valid base64")?;
    let signature = Signature::from_der(&signature_bytes).context("signature is not valid DER-encoded ECDSA")?;

    verifying_key
        .verify(content.as_bytes(), &signature)
        .context("signature does not match content — refusing to run it")
}

#[cfg(test)]
mod tests {
    use super::*;
    use p256::ecdsa::signature::Signer;
    use p256::ecdsa::SigningKey;
    use p256::pkcs8::EncodePublicKey;

    /// A fixed (not random) test keypair — deterministic input, no RNG dependency needed just to
    /// exercise signature verification.
    fn test_signing_key(seed: u8) -> SigningKey {
        SigningKey::from_bytes(&[seed; 32].into()).expect("32 fixed bytes is always a valid P-256 scalar for this seed")
    }

    #[test]
    fn admin_group_id_resolves_to_the_group_install_sh_uses() {
        // 80 on every macOS release to date. Asserted rather than hardcoded in the lookup itself
        // because getting this wrong does not fail loudly — it silently hands this host's private
        // key to whichever group holds that gid, and the only visible symptom would be the
        // per-user process working exactly as before.
        assert_eq!(super::admin_group_id(), Some(80));
    }

    #[test]
    fn admin_group_is_a_group_the_logged_in_user_is_actually_in() {
        // The whole point of the admin group here is that the per-user --agent process can read
        // the identity. If a future macOS moved administrators out of `admin`, this pins the
        // assumption rather than letting re-enrollment quietly lock that process out again.
        let gid = super::admin_group_id().expect("admin group should exist on macOS");
        let groups = std::process::Command::new("id").arg("-G").output().expect("id -G should run");
        let member_of: Vec<u32> = String::from_utf8_lossy(&groups.stdout)
            .split_whitespace()
            .filter_map(|g| g.parse().ok())
            .collect();
        assert!(
            member_of.contains(&gid) || member_of.contains(&0),
            "the user running these tests is in neither admin nor root: {member_of:?}"
        );
    }

    fn identity_with_public_key(signing_key: &SigningKey) -> AgentIdentity {
        AgentIdentity {
            certificate_pem: String::new(),
            private_key_pem: String::new(),
            artifact_signing_public_key_pem: signing_key
                .verifying_key()
                .to_public_key_pem(Default::default())
                .expect("encoding a P-256 public key to PEM never fails"),
        }
    }

    fn sign_base64(signing_key: &SigningKey, content: &str) -> String {
        let signature: Signature = signing_key.sign(content.as_bytes());
        base64::engine::general_purpose::STANDARD.encode(signature.to_der().as_bytes())
    }

    #[test]
    fn verify_artifact_signature_accepts_a_genuine_signature_over_the_exact_content() {
        let signing_key = test_signing_key(0x11);
        let identity = identity_with_public_key(&signing_key);
        let content = "#!/bin/sh\necho hello\n";

        let signature = sign_base64(&signing_key, content);

        assert!(verify_artifact_signature(&identity, content, &signature).is_ok());
    }

    #[test]
    fn verify_artifact_signature_rejects_tampered_content() {
        let signing_key = test_signing_key(0x11);
        let identity = identity_with_public_key(&signing_key);
        let signature = sign_base64(&signing_key, "#!/bin/sh\necho hello\n");

        let result = verify_artifact_signature(&identity, "#!/bin/sh\necho PWNED\n", &signature);

        assert!(result.is_err());
    }

    #[test]
    fn verify_artifact_signature_rejects_a_signature_produced_by_a_different_key() {
        let identity = identity_with_public_key(&test_signing_key(0x11));
        let other_key = test_signing_key(0x22);
        let signature = sign_base64(&other_key, "content");

        let result = verify_artifact_signature(&identity, "content", &signature);

        assert!(result.is_err());
    }

    #[test]
    fn verify_artifact_signature_rejects_invalid_base64() {
        let identity = identity_with_public_key(&test_signing_key(0x11));

        let result = verify_artifact_signature(&identity, "content", "not valid base64 !!!");

        assert!(result.is_err());
    }

    #[test]
    fn verify_artifact_signature_rejects_base64_that_isnt_a_der_signature() {
        let identity = identity_with_public_key(&test_signing_key(0x11));
        let bogus = base64::engine::general_purpose::STANDARD.encode(b"not a real der-encoded signature");

        let result = verify_artifact_signature(&identity, "content", &bogus);

        assert!(result.is_err());
    }

    #[test]
    fn verify_artifact_signature_rejects_an_unparsable_pinned_public_key() {
        let identity = AgentIdentity {
            certificate_pem: String::new(),
            private_key_pem: String::new(),
            artifact_signing_public_key_pem: "not a PEM-encoded key at all".to_string(),
        };

        let result = verify_artifact_signature(&identity, "content", "AAAA");

        assert!(result.is_err());
    }
}
