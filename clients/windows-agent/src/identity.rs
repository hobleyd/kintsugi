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
///
/// Three outcomes, and the distinction between the last two is the whole reason this returns a
/// `Result` rather than an `Option`: `Ok(Some)` is enrolled, `Ok(None)` is genuinely not enrolled
/// yet — so the caller should go and `enroll` — and `Err` is *enrolled but unreadable*, meaning the
/// files are on disk and this process cannot open them.
///
/// Collapsing those last two into one `None` is what turns a permissions fault into a long
/// diagnosis. The identity directory is restricted to SYSTEM and Administrators (see
/// `restrict_identity_permissions`), so a service running as anything else can read none of it —
/// and the agent would report "this agent has not enrolled an identity yet", re-enroll on every
/// check-in forever, burn a certificate issuance on the server each time, and then die on the
/// *write*. The file named in that failure is `agent.crt`, the first one written, which is not
/// necessarily the file whose read actually failed — so the error points somewhere unhelpful.
/// Only `NotFound` means "not enrolled"; anything else is a broken installation and says so.
pub fn load(dir: &Path) -> Result<Option<AgentIdentity>> {
    // Every file is attempted rather than short-circuiting on the first absent one: a directory
    // this process cannot read reports each of them as unreadable, and stopping early would hide
    // that behind whichever file happened to be missing.
    let certificate_pem = read_identity_file(dir, CERT_FILE)?;
    let private_key_pem = read_identity_file(dir, KEY_FILE)?;
    let artifact_signing_public_key_pem = read_identity_file(dir, ARTIFACT_PUBKEY_FILE)?;

    match (certificate_pem, private_key_pem, artifact_signing_public_key_pem) {
        (Some(certificate_pem), Some(private_key_pem), Some(artifact_signing_public_key_pem)) => {
            Ok(Some(AgentIdentity { certificate_pem, private_key_pem, artifact_signing_public_key_pem }))
        }
        _ => Ok(None),
    }
}

/// Reads one identity file, separating "not there" (`Ok(None)` — this agent has never enrolled)
/// from "there and refused" (`Err`). The counterpart to `write_identity_file`, and it names the
/// path for the same reason.
fn read_identity_file(dir: &Path, file_name: &str) -> Result<Option<String>> {
    let path = dir.join(file_name);
    match fs::read_to_string(&path) {
        Ok(contents) => Ok(Some(contents)),
        Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(err) => Err(anyhow::Error::new(err).context(format!("could not read {}", path.display()))),
    }
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
    write_identity_file(dir, CERT_FILE, &parsed.certificate_pem, "this agent's certificate")?;
    write_identity_file(dir, KEY_FILE, &private_key_pem, "this agent's private key")?;
    write_identity_file(dir, CA_FILE, &parsed.ca_certificate_pem, "the CA certificate")?;
    write_identity_file(dir, ARTIFACT_PUBKEY_FILE, &parsed.artifact_signing_public_key_pem, "the artifact-signing public key")?;
    restrict_identity_permissions(dir);

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
/// same trust source (the Windows root store, via rustls-native-certs, which is what
/// `rustls-tls-native-roots` gives the HTTP client and what an enterprise's internal CA is deployed
/// into) — so a host that can check in can also open this socket, and one that cannot fails both the
/// same way.
pub fn to_rustls_client_config(identity: &AgentIdentity) -> Result<std::sync::Arc<rustls::ClientConfig>> {
    let native = rustls_native_certs::load_native_certs();
    let mut roots = rustls::RootCertStore::empty();
    for certificate in native.certs {
        // Individually, ignoring failures: a Windows root store routinely contains certificates
        // rustls declines to parse, and one of those must not stop the other few hundred loading.
        let _ = roots.add(certificate);
    }

    if roots.is_empty() {
        anyhow::bail!("no trusted root certificates could be loaded from this host's own certificate store");
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

    match load(&dir) {
        Ok(Some(identity)) => return Some(identity),
        Ok(None) => {}
        // Enrolled, but this process cannot read what it enrolled with. Deliberately *not* followed
        // by an enrollment attempt: the write would be refused by the same permissions that just
        // refused the read, so trying would replace this precise diagnosis with a misleading one
        // about `agent.crt` — and would spend a certificate issuance on the server every check-in
        // to do it. Nothing here can fix the fault, so it is reported in the terms an administrator
        // can act on instead.
        Err(err) => {
            crate::logging::error(&format!(
                "this agent's identity is on disk but cannot be read, so it will present no certificate and every \
                 agent-only server request will be rejected: {err:#}. This is a permissions fault rather than a \
                 missing enrollment — the identity directory is restricted to SYSTEM and the local Administrators \
                 group, so check which account the service runs as (`sc.exe qc {}`). To start over: stop the \
                 service, delete the identity directory outright — the whole directory, not its contents, which is \
                 what clears stale per-file permissions — and start the service again.",
                crate::config::SERVICE_NAME
            ));
            return None;
        }
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

/// Writes one of the four identity files, naming the full path it failed on.
///
/// Worth a helper rather than four `context` strings because of what Windows reports when a write
/// is refused: `Access is denied. (os error 5)` and nothing else. That single message covers two
/// faults needing opposite fixes — a *directory* that won't accept a new file (the account this
/// process runs as is neither of the two SIDs `restrict_identity_permissions` grants, so the
/// agent's own hardening has locked it out; check `sc.exe qc KintsugiAgent`) and an *existing file*
/// that won't accept a rewrite (read-only attribute, or delete-pending because someone removed
/// `identity/` by hand to recover from a regenerated CA, which is the documented remedy). Naming
/// the path is what points an administrator at which of the two to go and look at; without it the
/// only visible symptom is the downstream 403, since a failed enrollment leaves `load_or_enroll`
/// returning `None` and `build_client` presenting no certificate at all.
fn write_identity_file(dir: &Path, file_name: &str, contents: &str, description: &str) -> Result<()> {
    let path = dir.join(file_name);
    fs::write(&path, contents).with_context(|| format!("failed to save {description} to {}", path.display()))
}

/// Locks the identity directory (and so the freshly written private key inside it) to SYSTEM and
/// the local Administrators group, dropping the inherited permissions that would otherwise let any
/// interactive user read it.
///
/// This is stricter than the macOS agent's equivalent, and it can be: there, the per-user process
/// reads this same identity to talk to the server itself, so the key has to stay readable by the
/// `admin` group. Here the tray process never talks to the server at all — it goes through the
/// queue (see `queue`) — so nothing unprivileged has any reason to open this.
///
/// Applied via `icacls` rather than by building a SECURITY_DESCRIPTOR by hand: this runs exactly
/// once per host, at enrollment, and the shelled-out form is the one an administrator can read in
/// the log and reproduce verbatim to check what was applied. Best-effort — a failure here leaves
/// the directory's inherited (still administrator-created) permissions in place rather than
/// aborting an otherwise successful enrollment, and is logged so it isn't silent.
fn restrict_identity_permissions(dir: &Path) {
    // /inheritance:r removes inherited entries first — without it, the grants below would be added
    // *alongside* whatever ProgramData already hands to Users, which is the whole thing being
    // removed. SIDs, not names: "SYSTEM" and "Administrators" are localized on a non-English
    // Windows install, and S-1-5-18 / S-1-5-32-544 never are.
    let output = std::process::Command::new("icacls")
        .arg(dir)
        .args(["/inheritance:r", "/grant:r", "*S-1-5-18:(OI)(CI)F", "/grant:r", "*S-1-5-32-544:(OI)(CI)F", "/T"])
        .output();

    match output {
        Ok(output) if output.status.success() => {
            crate::logging::info(&format!("restricted {} to SYSTEM and Administrators", dir.display()));
        }
        Ok(output) => crate::logging::warn(&format!(
            "could not restrict permissions on {}: icacls exited with {}: {}",
            dir.display(),
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        )),
        Err(err) => crate::logging::warn(&format!("could not run icacls to restrict {}: {err}", dir.display())),
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

    /// A scratch identity directory of its own per test, named the way the rest of this agent
    /// names temporary paths (see `policy`, `self_update`) since there is no tempfile dependency.
    fn scratch_dir(tag: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-identity-{tag}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        fs::create_dir_all(&dir).expect("creating a scratch directory under the temp dir always works");
        dir
    }

    #[test]
    fn load_reports_no_identity_when_nothing_has_been_enrolled_yet() {
        let dir = scratch_dir("absent");

        // Ok(None), not Err: a fresh install has no identity and that is not a fault.
        assert!(load(&dir).expect("an empty directory is not an error").is_none());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn load_reports_no_identity_when_only_some_of_the_files_are_present() {
        let dir = scratch_dir("partial");
        fs::write(dir.join(CERT_FILE), "cert").unwrap();
        fs::write(dir.join(KEY_FILE), "key").unwrap();
        // artifact-signing.pub missing — the pinned key `verify_artifact_signature` needs.

        assert!(load(&dir).expect("a missing file is not an error").is_none());

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn load_returns_the_identity_once_every_file_is_present() {
        let dir = scratch_dir("complete");
        fs::write(dir.join(CERT_FILE), "cert-pem").unwrap();
        fs::write(dir.join(KEY_FILE), "key-pem").unwrap();
        fs::write(dir.join(ARTIFACT_PUBKEY_FILE), "pub-pem").unwrap();

        let identity = load(&dir).expect("readable files are not an error").expect("all three files are present");

        assert_eq!(identity.certificate_pem, "cert-pem");
        assert_eq!(identity.private_key_pem, "key-pem");
        assert_eq!(identity.artifact_signing_public_key_pem, "pub-pem");

        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn read_identity_file_distinguishes_an_absent_file_from_an_unreadable_one() {
        // The distinction the whole signature exists for. A directory where a file's name is taken
        // by a *directory* is the portable stand-in for "present and refused" — opening it to read
        // fails with something that is not NotFound, exactly as a permissions refusal does.
        let dir = scratch_dir("unreadable");
        fs::create_dir_all(dir.join(CERT_FILE)).unwrap();

        assert!(read_identity_file(&dir, KEY_FILE).expect("an absent file is not an error").is_none());

        let err = read_identity_file(&dir, CERT_FILE).expect_err("a file that cannot be read is an error");
        assert!(err.to_string().contains(CERT_FILE), "the error should name the path it failed on: {err:#}");

        // And the error must travel all the way out of `load`, rather than being flattened back
        // into "not enrolled" — which is what sent the agent into a doomed re-enrollment loop.
        assert!(load(&dir).is_err());

        let _ = fs::remove_dir_all(&dir);
    }

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
