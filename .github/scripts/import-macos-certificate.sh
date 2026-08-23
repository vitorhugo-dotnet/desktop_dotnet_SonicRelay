#!/usr/bin/env bash
# Imports the Developer ID Application certificate into a throwaway keychain so
# codesign can use it during a macOS release job (issue #62).
#
# A dedicated keychain — rather than the runner's login keychain — keeps the
# private key out of the default search list for anything else running on the
# machine, and lets the job unlock it non-interactively. GitHub-hosted runners
# are ephemeral, but self-hosted ones are not, so the keychain is created with a
# random password and left unlocked only for this job's lifetime.
#
# Required environment:
#   MACOS_CERTIFICATE_P12_BASE64  base64-encoded .p12 containing cert + private key
#   MACOS_CERTIFICATE_PASSWORD    password protecting that .p12
set -euo pipefail

: "${MACOS_CERTIFICATE_P12_BASE64:?MACOS_CERTIFICATE_P12_BASE64 is required}"
: "${MACOS_CERTIFICATE_PASSWORD:?MACOS_CERTIFICATE_PASSWORD is required}"

keychain_path="$RUNNER_TEMP/sonicrelay-signing.keychain-db"
certificate_path="$RUNNER_TEMP/sonicrelay-signing.p12"
keychain_password="$(openssl rand -base64 24)"

cleanup() {
    # The .p12 holds the private key; never leave it on disk once imported.
    rm -f "$certificate_path"
}
trap cleanup EXIT

printf '%s' "$MACOS_CERTIFICATE_P12_BASE64" | base64 --decode > "$certificate_path"

security create-keychain -p "$keychain_password" "$keychain_path"
# Without this the keychain re-locks after the default idle timeout, and a long
# notarization wait would leave codesign prompting for a password nobody can answer.
security set-keychain-settings -lut 21600 "$keychain_path"
security unlock-keychain -p "$keychain_password" "$keychain_path"

# -T grants codesign access to the key; -A would grant it to every tool.
security import "$certificate_path" \
    -k "$keychain_path" \
    -P "$MACOS_CERTIFICATE_PASSWORD" \
    -T /usr/bin/codesign \
    -T /usr/bin/security

# Required on modern macOS: without an explicit partition list, codesign still
# triggers an interactive "allow access" prompt even for a key it owns.
security set-key-partition-list -S apple-tool:,apple: -k "$keychain_password" "$keychain_path" >/dev/null

# Prepend rather than replace, so system roots stay resolvable for verification.
security list-keychains -d user -s "$keychain_path" $(security list-keychains -d user | tr -d '"')

echo "Imported signing identities:"
security find-identity -v -p codesigning "$keychain_path"
