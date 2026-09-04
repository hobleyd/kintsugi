#!/usr/bin/env bash
# Writes the release notes for one agent release to stdout: the build summary the release job
# passes in, then every commit since that platform's previous release that touched the agent's own
# tree. Called from the three "Publish release" steps in ci.yml, on macOS, Windows (git bash) and
# Linux runners alike, which is why it is one script here rather than three `git log` invocations
# written into the YAML.
#
# The previous release is the highest existing `<platform>-agent-v*` tag other than the one about
# to be created — highest rather than most recent, since a re-run after a failed publish may find
# the current tag already present. The paths are the agent's crate, plus the Wayland backend for
# Linux because it ships in the same archive and is replaced by the same self-update; a commit that
# touches all three agents (remote_protocol.rs is kept identical across them) is correctly listed
# under each. Nothing that touched only the server or the admin UI appears, because it is not in
# the build.
#
# What this reads is the Clients screen's "release notes for every newer build" — the server
# imports the body (truncated at 2000 characters) and the screen shows what GitHub holds. One line
# per commit keeps a release of any plausible size under that limit; the bodies, which are where
# this repository's reasoning lives, are a `git log` away via the short hash.
#
# Needs the full history and tags: the calling job checks out with `fetch-depth: 0`.
set -euo pipefail

if [[ $# -ne 3 ]]; then
    echo "usage: $0 <macos|windows|linux> <version> <summary>" >&2
    exit 2
fi

platform=$1
version=$2
summary=$3

prefix="${platform}-agent-v"
paths=("clients/${platform}-agent")
if [[ $platform == linux ]]; then
    paths+=("clients/linux-agent-wayland")
fi

previous="$(git tag -l "${prefix}*" --sort=-v:refname | grep -vx "${prefix}${version}" | head -n 1 || true)"

printf '%s\n\n' "$summary"

if [[ -n $previous ]]; then
    printf '## Changes since %s\n\n' "${previous#"$prefix"}"
    range="${previous}..HEAD"
else
    printf '## Changes\n\n'
    range="HEAD"
fi

changes="$(git log "$range" --no-merges --format='- %s (%h)' -- "${paths[@]}")"
if [[ -n $changes ]]; then
    printf '%s\n' "$changes"
else
    printf -- '- No changes to the agent since the previous release; rebuilt from the current tree.\n'
fi
