namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Builds the fixed --appName/--appId/--update-version/--update script for a Flatpak-managed
/// application (<paramref name="isSelfUpdate"/> selects Flatpak's own row over one of the
/// applications it manages). Shared between the "Find Upgrade Paths" research flow
/// (<see cref="Commands.ResearchApplicationUpgradePath.ResearchApplicationUpgradePathCommandHandler"/>)
/// and registration-time seeding (<c>RegisterApplicationsCommandHandler</c>) so both only ever
/// produce this one script shape — never AI-generated, since Flatpak's upgrade mechanics are
/// already fully known and there's nothing to research.
/// </summary>
/// <remarks>
/// The application is never baked into the returned text — both the server (via
/// <c>IUpgradePathResearchClient.CheckScriptVersionAsync</c>) and the Linux agent (via
/// <c>patch_one</c>) invoke every script with <c>--appName</c>/<c>--appId</c> as real arguments, so
/// the script reads them at runtime instead. That means <see cref="Build"/> returns byte-identical
/// content for every Flatpak application, and a human only ever needs to review and sign one script
/// per case — see <c>IUpgradePathRepository.FindExistingSignatureForScriptAsync</c>.
///
/// Flatpak qualifies for <see cref="PackageManagerCatalog"/> at all because Flathub publishes a
/// global catalog over plain HTTP: <c>--update-version</c> has to run on the API server rather than
/// the managed host, and "the latest version of this application" is a question Flathub can answer
/// from anywhere. The distribution's own package manager (apt, dnf, ...) cannot — its answer is
/// specific to one host's configured repositories — which is why dpkg/rpm packages are handled as
/// OS updates instead and never appear as applications at all. See the Linux agent's
/// <c>main::collect_installed_applications</c>.
///
/// System installations only. A <c>flatpak --user</c> install lives in one user's home directory
/// and the agent's root service could not patch it, so the agent never reports one — see
/// <c>system_info::scan_flatpak</c>.
/// </remarks>
public static class FlatpakUpgradeScript
{
    public static string Build(bool isSelfUpdate)
    {
        // Flatpak itself is not a Flatpak — it is a distribution package — and that makes its own
        // row the one case in this catalog with no answerable version question.
        //
        // The obvious implementation (read the latest tag from github.com/flatpak/flatpak) is
        // actively harmful, and was written and then removed here rather than shipped. It reports
        // what upstream released; `--update` can only install what this host's repositories carry,
        // and those are years apart — Debian 12 ships 1.14.10 against an upstream 1.18.2. The row
        // would say "update available" forever, `--update` would exit 0 having changed nothing, the
        // agent would report the upstream version as installed, and the next inventory would
        // contradict it. A patch that always succeeds and never changes anything is worse than no
        // patch at all, because nothing looks wrong.
        //
        // So this mode declines to answer. `LatestVersion` stays null, which means
        // `updateAvailable` is false, which means the agent never tries — and flatpak still gets
        // patched, by the same `apt-get upgrade`/`dnf upgrade` that patches every other
        // distribution package (see the Linux agent's `os_update`). This is the same reasoning that
        // keeps apt and dnf themselves out of PackageManagerCatalog, applied to the one member that
        // turns out to be one of their packages.
        var latestVersionLogic = isSelfUpdate
            ? """
              latest_version() {
                echo "flatpak is a distribution package on this host, so there is no single upstream" >&2
                echo "version to compare against — its updates arrive with the operating system's own" >&2
                echo "updates. Nothing to report." >&2
                return 1
              }
              """
            : """
              latest_version() {
                local response
                response=$(curl -fsSL "https://flathub.org/api/v2/appstream/${APP_ID}") || return 1

                # The newest stable release is the first entry of the "releases" array. Everything
                # before that key is dropped first so an earlier "version" field elsewhere in the
                # document can never be picked up by mistake, and the value is read with grep/sed
                # because jq is not something the API server is guaranteed to have.
                local version
                version=$(printf '%s' "$response" \
                  | sed -E 's/.*"releases"://' \
                  | grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' \
                  | head -1 \
                  | sed -E 's/.*"([^"]*)"$/\1/')

                [ -n "$version" ] || return 1
                printf '%s' "$version"
              }
              """;

        var updateCommand = isSelfUpdate
            ? """
              # Reachable only if a human runs this script directly: the agent never gets here,
              # because latest_version above declines to answer and so nothing is ever marked
              # updatable. Kept because it is still the correct way to upgrade flatpak on demand,
              # and because a script whose --update mode did nothing would be the more confusing
              # artifact to find. It has to work on whichever distribution this host happens to be
              # — never assume apt.
              export DEBIAN_FRONTEND=noninteractive
              if command -v apt-get >/dev/null 2>&1; then
                apt-get update -qq
                apt-get install -y --only-upgrade flatpak
              elif command -v dnf >/dev/null 2>&1; then
                dnf -y upgrade flatpak
              elif command -v zypper >/dev/null 2>&1; then
                zypper --non-interactive update flatpak
              elif command -v pacman >/dev/null 2>&1; then
                pacman -S --noconfirm flatpak
              elif command -v apk >/dev/null 2>&1; then
                apk upgrade flatpak
              else
                echo "no supported distribution package manager found to upgrade flatpak with" >&2
                exit 1
              fi
              """
            : """
              # --system, matching what the agent reports: a --user installation belongs to one
              # person's home directory and this script runs as root.
              flatpak update --system --noninteractive --assumeyes "$APP_ID"
              """;

        return $$"""
            #!/bin/bash
            set -euo pipefail

            usage() {
              echo "Usage: $0 --appName <name> --appId <id> (--update-version|--update)" >&2
              exit 1
            }

            APP_ID=""
            MODE=""
            while [ $# -gt 0 ]; do
              case "$1" in
                --appName) shift 2 ;;
                --appId) APP_ID="$2"; shift 2 ;;
                --update-version) [ -n "$MODE" ] && usage; MODE="update-version"; shift ;;
                --update) [ -n "$MODE" ] && usage; MODE="update"; shift ;;
                *) usage ;;
              esac
            done
            [ -n "$APP_ID" ] || usage
            [ -n "$MODE" ] || usage

            {{latestVersionLogic}}

            if [ "$MODE" = "update-version" ]; then
              version=$(latest_version) || { echo "could not determine the latest version" >&2; exit 1; }
              printf '%s\n' "$version"
              exit 0
            fi

            # --update mode: runs on the managed host itself, as root from a systemd service.
            if ! command -v flatpak >/dev/null 2>&1; then
              echo "flatpak is not installed" >&2
              exit 1
            fi

            {{updateCommand}}
            """;
    }
}
