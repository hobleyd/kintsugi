namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for everything Homebrew
/// manages, Homebrew itself included. Shared between the "Find Upgrade Paths" research flow
/// (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both only ever
/// produce this one script shape under the <see cref="PlatformBucket.ForPackageManager"/> bucket the
/// scan planner expects for every package-manager-managed row — never AI-generated, since
/// Homebrew's upgrade mechanics are already fully known and there's nothing to research.
/// The package name is never baked into the returned text — both the server (via
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and the macOS agent (via
/// <c>patch_one</c>) already invoke every script with <c>--appName &lt;name&gt;</c> as a real
/// argument, so the script reads it at runtime instead. That means <see cref="Build"/> returns
/// byte-identical content for every formula and cask — a human only ever needs to review and sign
/// one script, and every other row sharing that exact content inherits the same signature
/// automatically (see <c>ResearchApplicationUpgradePathCommandHandler.UpsertAsync</c>,
/// <c>RegisterApplicationsCommandHandler.UpsertPackageManagerUpgradePathsAsync</c>, and
/// <c>SignUpgradePathScriptCommandHandler</c>) rather than needing its own separate review.
/// --update-version runs under plain bash + curl against Homebrew's own public API so it works
/// unattended on this (Linux) server, exactly like the AI-authored contract requires; --update runs
/// the actual `brew` commands, which only ever exist on the managed Mac.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="SnapUpgradeScript"/>, this is one text for Homebrew's own row and every formula
/// it manages (see <see cref="RecognizedPackageManager.BuildScript"/>) — but for the opposite
/// reason. snapd is itself a snap, so one text serves both cases
/// by accident of the platform; Homebrew is <em>not</em> a formula, and its own row used to get a
/// second script for that reason. That second script cost more than it bought: the Applications
/// screen nests every formula and cask under the "Homebrew" row and shows the manager's script
/// there once, on behalf of all of them, and a row whose own bytes differ from its children's is
/// exactly the case that presentation cannot make honest. So the one script tells Homebrew's own
/// row apart at <em>runtime</em>, by <c>--appName</c> — the same name the agent reports it under
/// (<c>system_info::HOMEBREW_NAME</c>) — and answers its version from GitHub's releases redirect
/// rather than the formula API. The rows still share bytes, so one signature covers Homebrew and
/// everything it manages, which is what lets the manager's row stand in for its children.
/// </para>
/// <para>
/// Every --update also upgrades Homebrew itself, deliberately: <c>brew update</c> is Homebrew's own
/// self-update (it pulls brew's repository to its latest release tag and migrates) as well as the
/// index refresh <c>brew upgrade</c> needs to see a version released since the last one. Homebrew's
/// own row runs only that step — the applications each have a row of their own to be upgraded
/// through, once a human has approved them, and a blanket <c>brew upgrade</c> here would patch every
/// formula on the host regardless.
/// </para>
/// </remarks>
public static class HomebrewUpgradeScript
{
    public static string Build()
    {
        return """
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

            # Homebrew's own row. The agent reports Homebrew itself as an application named "Homebrew"
            # (see system_info::HOMEBREW_NAME in the macOS agent), and it is not a formula: neither
            # URL below knows it, and `brew upgrade homebrew` would be an error. One script serves it
            # and everything it manages so that one review covers all of them; this is where the two
            # part ways.
            is_homebrew_itself() {
              [ "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')" = "homebrew" ]
            }

            # Both URLs are case-sensitive, and $APP_NAME is not always the lowercase token brew
            # knows the package by: PrepareUpgradePathScanQueryHandler groups an application's
            # variants case-insensitively, so a row whose name settled on a display-cased
            # /Applications bundle ("Nextcloud") rather than on `brew list`'s output ("nextcloud")
            # 404s on every URL below and leaves LatestVersion null — which makes updateAvailable
            # false, which makes the agent's is_patchable false, so the application silently never
            # patches. `brew` itself downcases its argument, so only this lookup is affected.
            # Tried as given first: a row already named by its token must not be transformed.
            latest_version() {
              local candidate response version redirect

              if is_homebrew_itself; then
                # -fsS, deliberately NOT -fsSL: %{redirect_url} reports the redirect curl did *not*
                # follow, so adding -L makes curl follow it to the tag page and report an empty
                # string — the check then fails, LatestVersion stays null, and Homebrew's own row
                # silently never updates.
                redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' "https://github.com/Homebrew/brew/releases/latest") || return 1
                [ -n "$redirect" ] || return 1
                printf '%s' "${redirect##*/}"
                return 0
              fi

              for candidate in "$APP_NAME" "$(printf '%s' "$APP_NAME" | tr '[:upper:]' '[:lower:]')"; do
                response=$(curl -fsSL "https://formulae.brew.sh/api/formula/${candidate}.json" 2>/dev/null) || \
                  response=$(curl -fsSL "https://formulae.brew.sh/api/cask/${candidate}.json" 2>/dev/null) || continue

                version=$(printf '%s' "$response" | grep -o '"stable":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                if [ -z "$version" ]; then
                  version=$(printf '%s' "$response" | grep -o '"version":"[^"]*"' | head -1 | sed -E 's/.*:"([^"]*)"/\1/')
                fi

                [ -n "$version" ] || continue
                printf '%s' "$version"
                return 0
              done

              return 1
            }

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

            # `brew update` is Homebrew upgrading itself — it pulls brew's own repository to its latest
            # release tag — and is also what refreshes the formula/cask index; without it `brew upgrade`
            # acts on a stale catalog and reports "already up to date" for a version released since the
            # index was last refreshed. So every application upgrade brings Homebrew along, and for
            # Homebrew's own row it is the whole upgrade: the applications each have a row of their own.
            brew update

            if is_homebrew_itself; then
              exit 0
            fi

            brew upgrade "$APP_NAME"
            """;
    }
}
