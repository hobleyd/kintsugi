---
paths:
  - "src/Kintsugi.Infrastructure/**"
  - "src/Kintsugi.Application/**"
  - "approved-scripts/**"
---

# Couplings: the script-approval repository

- The script-approval token is deliberately *not* the read-only API token. The latter exists only to
  lift GitHub's anonymous rate limit and is handed to the AI research client and the agent-package
  source client as well, so reusing it would silently give both of them `contents:write` and
  `pull_requests:write` on the approval repository. Unset means signing approves locally and raises
  no pull request — the Upgrade Scripts screen says so, because the absence of an audit trail is
  otherwise only discoverable by looking for pull requests that were never opened.
- `ApprovedScriptCorpus` is the *only* description of the approval repository's layout, and both ends
  of the round trip go through it — the publisher writing an entry and the reader parsing one. A path
  or field changed on one side only means an approval that publishes fine and imports as nothing.
- GitHub's `/tarball/{ref}` nests everything under a `{owner}-{repo}-{shortsha}/` directory.
  `ReadArchiveFiles` strips that first segment; without it nothing matches `approved-scripts/` and
  the result is indistinguishable from an empty corpus.
