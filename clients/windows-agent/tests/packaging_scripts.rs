//! The shipped PowerShell must stay pure ASCII.
//!
//! `packaging/install.ps1` and `packaging/uninstall.ps1` travel inside the release tarball and are
//! run by Windows PowerShell 5.1 - both by hand and by the bootstrap script the server renders in
//! `src/Kintsugi.Application/AgentPackages/WindowsBootstrapScript.cs`, which invokes them as
//! `powershell.exe -File`. PowerShell 5.1 decodes a `.ps1` with no byte-order mark as cp1252, not
//! UTF-8, so a UTF-8 em dash (`E2 80 94`) arrives as the three characters `a-hat`, `euro`, and
//! U+201D - and U+201D is a character PowerShell honours as a double-quote delimiter. One em dash
//! inside a string literal therefore closes that string early and the parse cascades into
//! "Missing closing '}'" errors pointing at lines nowhere near the real one. That shipped in
//! v0.11.0: two em dashes in `Write-Warning` strings made the installer unparseable on every
//! Windows host it reached.
//!
//! A byte-order mark would also fix it, but a BOM is invisible and any editor that normalises the
//! file back to BOM-less UTF-8 silently re-arms the bug, which only surfaces on a real Windows host
//! at install time. Staying ASCII is the load-bearing rule; this test is what keeps it.
//!
//! `packaging/config.toml` is deliberately not covered: it must stay BOM-less *and* it is not
//! PowerShell - see the comment in install.ps1 where it copies the file, and the matching
//! `UTF8Encoding($false)` in WindowsBootstrapScript.cs.

use std::path::Path;

#[test]
fn packaging_powershell_is_ascii_only() {
    let packaging = Path::new(env!("CARGO_MANIFEST_DIR")).join("packaging");

    for name in ["install.ps1", "uninstall.ps1", "publish-release.ps1"] {
        let path = packaging.join(name);
        let bytes = std::fs::read(&path).unwrap_or_else(|e| panic!("reading {}: {e}", path.display()));

        let mut line = 1usize;
        let mut column = 1usize;
        for &byte in &bytes {
            assert!(
                byte < 0x80,
                "{name}:{line}:{column} has the non-ASCII byte 0x{byte:02x}. Windows PowerShell 5.1 \
                 reads a BOM-less .ps1 as cp1252, where 0x94 becomes the smart quote U+201D and \
                 PowerShell treats that as a string delimiter - so this breaks the parse on every \
                 host the release tarball reaches. Use plain ASCII punctuation (- instead of an em \
                 dash).",
            );

            if byte == b'\n' {
                line += 1;
                column = 1;
            } else {
                column += 1;
            }
        }
    }
}
