---
paths:
  - "clients/*/src/remote_protocol.rs"
  - "web/lib/data/models/remote_control_mapper.dart"
  - "web/test/data/remote_control_mapper_test.dart"
---

# Couplings: the remote-control media protocol

This is the one hand-mirrored pair with **nothing between its two ends** — the server relays these
bytes without parsing them, so no C# definition sits in the middle and nothing server-side can ever
notice a mismatch. This rule loads at either end — each agent's remote_protocol.rs and the viewer's
remote_control_mapper.dart — deliberately: change one and you must change the other.

- **The remote-control media protocol is the one hand-mirrored pair with nothing between the two
  ends.** Every other mirrored shape in this repo (the Rust request structs, `web/lib/data/models/`)
  has a C# definition sitting between them, so a mismatch is at least visible in one place. This one
  is agent-to-browser directly — `clients/macos-agent/src/remote_protocol.rs` and
  `web/lib/data/models/remote_control_mapper.dart` — and the server relays the bytes without
  parsing them, so nothing server-side can ever notice. The tile header is big-endian because that
  is `ByteData`'s default on the reading side; get it wrong and the picture still draws, just
  scrambled. `web/test/data/remote_control_mapper_test.dart` asserts the exact bytes the agent's own
  test emits, and is the only check that exists.
- **The shell half of the media protocol is hand-mirrored the same way the tile half is, and has
  the same nothing between its two ends.** `encode_shell_output`/`decode_shell_input` in each
  agent's `remote_protocol.rs` and `remoteShellOutputFromBytes`/`remoteShellInputToBytes` in
  `web/lib/data/models/remote_control_mapper.dart` are the whole description of it; the server
  relays those bytes without parsing them, so a mismatch is invisible everywhere else. The two
  leading bytes (version, kind) are what keeps a tile from being typed into a shell and a keystroke
  from being drawn as a picture, which is why a shell frame carries them despite needing no
  geometry. `web/test/data/remote_control_mapper_test.dart` asserts the exact bytes the agents'
  own tests emit and accept, and is the only check that exists.
- **Terminal output crosses the wire as bytes and must not be "simplified" to a string.** The agent
  sends whatever the PTY produced when it produced it, so a frame can end mid-codepoint; decoding
  per frame turns any character unlucky enough to straddle a boundary into a replacement mark. The
  viewer holds **one** `Utf8Decoder` for the whole session (`remote_shell_view.dart`) for exactly
  that reason.
- **The display picker is a third hand-mirrored pair with nothing between its two ends**, alongside
  the tile half and the shell half above. `DisplayOption`/`DisplayInfo.displays` and
  `ViewerInput::SelectDisplay` in each agent's `remote_protocol.rs`, and `RemoteDisplayOption` /
  `RemoteDisplaySelection` in `web/lib/data/models/remote_control_mapper.dart`. Two strings are
  deliberately *not* the same and must stay apart: the geometry message's own `type` is `display`,
  travelling agent-to-browser, while the selection is `select-display`, travelling the other way
  through a different parser. Both sides' tests assert that the wrong one is refused.
- All three agents' `remote_protocol.rs` are copies of one another and must stay so. `diff` any two
  and exactly one line should differ outside the module comment — the cross-reference naming
  `virtual_key_for_hid`, `scan_code_for_hid` or `xtest_keycode_for_hid`, because the three platforms
  reach the same positional key through differently-named APIs. Anything else in that diff is drift,
  and since the server relays the media protocol without parsing it, nothing else would notice.
- The Wayland helper's own `FormatMessage.node_id` and `DisplayEntry` (`wire.rs`) are mirrored by
  `StreamFormat` and `HelperDisplay` in the agent's `wayland_backend.rs`, the same way the rest of
  that protocol is. A rename on one side alone means a picker that offers nothing — read deliberately
  as non-fatal, so nothing anywhere reports it.
