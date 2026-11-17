/// One address per line; commas and semicolons also separate, so a pasted list works. An entry
/// pasted from a mail client as `Alex Admin <alex.admin@corp.example.com>` is reduced to the address
/// inside the angle brackets; a plain address is left as it is. Blanks are dropped here, and the
/// server lower-cases and de-duplicates.
///
/// The reduction is not cosmetic. The server refuses a display name
/// (`UpdateAuthenticationSettingsCommandValidator`), so without it a pasted list is rejected
/// whole — and a display name containing a comma, which this splits on, still leaves a stray
/// fragment that the server then names under the field, rather than saving something that can
/// never match a sign-in.
///
/// Shared by the Authentication screen's list and the Administrators page, which write the same
/// list and must read what is pasted into them the same way.
List<String> parseAdminEmails(String text) => text
    .split(RegExp(r'[\n,;]'))
    .map((entry) => RegExp(r'<([^<>]*)>').firstMatch(entry)?.group(1) ?? entry)
    .map((entry) => entry.trim())
    .where((entry) => entry.isNotEmpty)
    .toList();
