//! Everything the release tarball ships must stay pure ASCII.
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
//! `packaging/config.toml` is covered for a related but distinct reason. It is not PowerShell, so
//! it cannot be mis-parsed - but both install.ps1 and the bootstrap script read-modify-write it
//! with `Get-Content`, whose default encoding in PS 5.1 is cp1252, and write it back as UTF-8. Each
//! pass therefore re-encodes every non-ASCII character into its own mojibake, twice per install and
//! compounding across reinstalls; a config.toml was found in the field with em dashes mangled three
//! deep. Both reads now pass `-Encoding UTF8`, but an ASCII source is what makes the two encodings
//! agree byte-for-byte and the question moot.
//!
//! config.toml must also stay *BOM-less* - config.rs parses it with the `toml` crate, which rejects
//! a leading U+FEFF, and Config::load_from falls back to built-in defaults on a parse failure, so a
//! BOM would show up as an agent silently ignoring the address just set for it. The assertion below
//! covers that too: a BOM is the bytes `EF BB BF`, all three above 0x7F.

use std::path::Path;

#[test]
fn packaged_files_are_ascii_only() {
    let packaging = Path::new(env!("CARGO_MANIFEST_DIR")).join("packaging");

    for name in ["install.ps1", "uninstall.ps1", "publish-release.ps1", "config.toml"] {
        let path = packaging.join(name);
        let bytes = std::fs::read(&path).unwrap_or_else(|e| panic!("reading {}: {e}", path.display()));

        let mut line = 1usize;
        let mut column = 1usize;
        for &byte in &bytes {
            assert!(
                byte < 0x80,
                "{name}:{line}:{column} has the non-ASCII byte 0x{byte:02x}. Windows PowerShell 5.1 \
                 reads these files as cp1252, not UTF-8. In a .ps1 that turns an em dash into the \
                 smart quote U+201D, which PowerShell honours as a string delimiter and which \
                 breaks the parse on every host the release tarball reaches; in config.toml it \
                 turns each read-modify-write pass into another layer of mojibake. Use plain ASCII \
                 punctuation (- instead of an em dash).",
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
