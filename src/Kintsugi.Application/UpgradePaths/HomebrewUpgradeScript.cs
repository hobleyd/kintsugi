namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for a Homebrew-managed
/// application (<paramref name="isSelfUpdate"/> selects Homebrew's own row, e.g. the "Homebrew"
/// application itself, over an individual formula/cask). Shared between the "Find Upgrade Paths"
/// research flow (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both ever only
/// ever produce this one script shape, under the one <see cref="PlatformBucket.Generic"/> bucket
/// the scan planner expects for every package-manager-managed row — never AI-generated, since
/// Homebrew's upgrade mechanics are already fully known and there's nothing to research.
/// The package name is never baked into the returned text — both the server (via
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and the macOS agent (via
/// <c>patch_one</c>) already invoke every script with <c>--appName &lt;name&gt;</c> as a real
/// argument, so the script reads it at runtime instead. That means <see cref="Build"/> returns
/// byte-identical content for every formula/cask (and a second, equally identical, content for the
/// self-update case) — a human only ever needs to review and sign one script per case, and every
/// other row sharing that exact content can inherit the same signature automatically (see
/// <c>ResearchApplicationUpgradePathCommandHandler.UpsertAsync</c>,
/// <c>RegisterApplicationsCommandHandler.UpsertPackageManagerUpgradePathsAsync</c>, and
/// <c>SignUpgradePathScriptCommandHandler</c>) rather than needing its own separate review.
/// --update-version runs under plain bash + curl against Homebrew's own public API (or, for the
/// self-update case, GitHub's releases redirect for Homebrew/brew itself) so it works unattended
/// on this (Linux) server, exactly like the AI-authored contract requires; --update runs the
/// actual `brew` commands, which only ever exist on the managed Mac.
/// </summary>
public static class HomebrewUpgradeScript
{
    public static string Build(bool isSelfUpdate)
    {
        var latestVersionLogic = isSelfUpdate
            ? """
              latest_version() {
                local redirect
                redirect=$(curl -fsSL -o /dev/null -w '%{redirect_url}' "https://github.com/Homebrew/brew/releases/latest") || return 1
                [ -n "$redirect" ] || return 1
                printf '%s' "${redirect##*/}"
              }
              """
            : """
              latest_version() {
                local response
                response=$(curl -fsSL "https://formulae.brew.sh/api/formula/${APP_NAME}.json" 2>/dev/null) || \
                  response=$(curl -fsSL "https://formulae.brew.sh/api/cask/${APP_NAME}.json" 2>/dev/null) || return 1

                local version
                version=$(printf '%s' "$response" | grep -o '"stable":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                if [ -z "$version" ]; then
                  version=$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                fi

                [ -n "$version" ] || return 1
                printf '%s' "$version"
              }
              """;

        // `brew update` refreshes Homebrew's own formula/cask index (and, for the self-update case,
        // Homebrew itself) before the actual upgrade runs — without it, `brew upgrade` can act on a
        // stale catalog and report "already up to date" for a version that was released since the
        // index was last refreshed.
        var updateCommand = isSelfUpdate ? "brew update && brew upgrade" : "brew update && brew upgrade \"$APP_NAME\"";

        return $$"""
            #!/bin/bash
            set -euo pipefail

            usage() {
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
              exit 1
            }

            APP_NAME=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) APP_NAME="$2"; shift 2 ;;
                --appId) shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
                *) usage ;;
              esac
            done
            [ -n "$APP_NAME" ] || usage
            [ -n "$MODE" ] || usage

            {{latestVersionLogic}}

            if [ "$MODE" = "update-version" ]; then
              version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
              printf '%s\n' "$version"
              exit 0
            fi

            # --update mode: runs on the managed Mac itself, where `brew` actually exists.
            if ! command -v brew >/dev/null 2>&1; then
              echo "homebrew is not installed" >&2
              exit 1
            fi

            {{updateCommand}}
            """;
    }
}
