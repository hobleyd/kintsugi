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
/// Builds a rustls `ClientConfig` presenting this host's certificate, for the one thing that cannot
/// go through `reqwest`: the remote control WebSocket.
///
/// `reqwest` does all of this internally for a `reqwest::Identity`, but it does not speak WebSocket,
/// and `tungstenite` wants a `ClientConfig` rather than a PEM buffer. Same certificate, same key,
/// same trust source — so a host that can check in can also open this socket, and one that cannot
/// fails both the same way.
pub fn to_rustls_client_config(identity: &AgentIdentity) -> Result<std::sync::Arc<rustls::ClientConfig>> {
    let native = rustls_native_certs::load_native_certs();
    let mut roots = rustls::RootCertStore::empty();
    for certificate in native.certs {
        // Individually, ignoring failures: a distribution's CA bundle routinely contains
        // certificates rustls declines to parse, and one of those must not stop the rest loading.
        let _ = roots.add(certificate);
    }

    if roots.is_empty() {
        anyhow::bail!("no trusted root certificates could be loaded from this host's CA bundle");
    }

    let certificate_chain = rustls_pemfile::certs(&mut identity.certificate_pem.as_bytes())
        .collect::<Result<Vec<_>, _>>()
        .context("could not parse this host's certificate")?;

    let private_key = rustls_pemfile::private_key(&mut identity.private_key_pem.as_bytes())
        .context("could not parse this host's private key")?
        .context("this host's key file contains no private key")?;

    let config = rustls::ClientConfig::builder()
        .with_root_certificates(roots)
        .with_client_auth_cert(certificate_chain, private_key)
        .context("could not build a TLS configuration from this host's certificate and key")?;

    Ok(std::sync::Arc::new(config))
}

pub fn build_client(timeout: std::time::Duration, identity: Option<&AgentIdentity>) -> Result<reqwest::blocking::Client> {
    let mut builder = reqwest::blocking::Client::builder().timeout(timeout);
    if let Some(identity) = identity {
        builder = builder.identity(to_reqwest_identity(identity)?);
    }
    builder.build().context("failed to build HTTP client")
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
fn restrict_key_permissions(path: &Path) {
    use std::os::unix::fs::PermissionsExt;
    if let Ok(metadata) = fs::metadata(path) {
        let mut permissions = metadata.permissions();
        // Owner (root, via the service that enrolls) read/write, and nothing for anyone else.
        // The macOS agent has to leave this group-readable because its per-user process makes
        // authenticated requests directly; here that process makes none at all — it goes through
        // `queue` — so no non-root reader exists to accommodate. See `config::IDENTITY_DIR`.
        permissions.set_mode(0o600);
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
