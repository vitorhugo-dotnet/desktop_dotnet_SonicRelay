#!/usr/bin/env bash
# Adds the macOS assets to an existing GitHub Release and folds their checksums
# into the release's single canonical checksums file (issue #62).
#
# Shared by .github/workflows/ci.yml and .github/workflows/release.yml so the two
# cannot drift, and so both go through retry_gh: this runs at the end of a long,
# expensive build, and a transient api.github.com 5xx must not discard packages
# that were all produced successfully.
#
# Required environment:
#   GH_TOKEN, RELEASE_TAG, RELEASE_VERSION
set -euo pipefail

: "${RELEASE_TAG:?RELEASE_TAG is required}"
: "${RELEASE_VERSION:?RELEASE_VERSION is required}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
dist_dir="${MACOS_DIST_DIR:-artifacts/release}"

# shellcheck source=.github/scripts/gh-retry.sh
source "$repo_root/.github/scripts/gh-retry.sh"

# Merge into the checksums file the Windows job created, so there is one
# canonical checksums file per release covering every platform's assets.
retry_gh gh release download "$RELEASE_TAG" --pattern checksums-sha256.txt --dir "$dist_dir" --clobber
( cd "$dist_dir" && shasum -a 256 SonicRelay-MacPublisher-*.zip SonicRelay-MacPublisher-*.dmg >> checksums-sha256.txt )

currentNotes=$(retry_gh gh release view "$RELEASE_TAG" --json body --jq .body)
macosNotes=$(printf '\n\n## macOS assets\n\n- `SonicRelay-MacPublisher-osx-arm64-%s.dmg`: Apple Silicon disk image (drag SonicRelay.app to Applications).\n- `SonicRelay-MacPublisher-osx-arm64-%s.zip`: Apple Silicon app bundle archive.\n- `SonicRelay-MacPublisher-osx-x64-%s.dmg`: Intel disk image.\n- `SonicRelay-MacPublisher-osx-x64-%s.zip`: Intel app bundle archive.\n\nmacOS 14 (Sonoma) or later. On first capture, SonicRelay asks for Screen & System Audio Recording permission — macOS gates system audio capture behind that grant. See `docs/macos-publisher.md` for installation, permissions, and known limitations.\n' "$RELEASE_VERSION" "$RELEASE_VERSION" "$RELEASE_VERSION" "$RELEASE_VERSION")

retry_gh gh release edit "$RELEASE_TAG" --notes "${currentNotes}${macosNotes}"

retry_gh gh release upload "$RELEASE_TAG" \
    "$dist_dir"/SonicRelay-MacPublisher-*.zip \
    "$dist_dir"/SonicRelay-MacPublisher-*.dmg \
    "$dist_dir/checksums-sha256.txt" \
    --clobber
