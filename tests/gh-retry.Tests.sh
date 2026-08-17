#!/usr/bin/env bash
# Behavioural tests for .github/scripts/gh-retry.sh.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=../.github/scripts/gh-retry.sh
source "$root/.github/scripts/gh-retry.sh"

export GH_RETRY_INITIAL_DELAY_SECONDS=0

state=$(mktemp -d)
trap 'rm -rf "$state"' EXIT

failures=0

assert() {
  if [[ "$2" == "$3" ]]; then
    echo "PASS: $1"
  else
    echo "FAIL: $1 (expected '$3', got '$2')"
    failures=$((failures + 1))
  fi
}

# Fails with a 5xx for the first $1 attempts, then succeeds and writes to stdout.
flaky_gh() {
  local attempts
  attempts=$(( $(cat "$state/count" 2>/dev/null || echo 0) + 1 ))
  echo "$attempts" > "$state/count"

  if (( attempts <= $1 )); then
    echo "HTTP 503: No server is currently available to service your request." >&2
    return 1
  fi

  echo 'release-body-from-github'
}

# Fails for a reason that retrying cannot fix.
fatal_gh() {
  local attempts
  attempts=$(( $(cat "$state/count" 2>/dev/null || echo 0) + 1 ))
  echo "$attempts" > "$state/count"
  echo 'release not found' >&2
  return 1
}

echo '--- Transient 5xx responses are retried ---'
rm -f "$state/count"
captured=$(retry_gh flaky_gh 2 2>/dev/null)
assert 'Retries until the command succeeds.' "$(cat "$state/count")" '3'
assert 'Only the command stdout is captured, not the retry warnings.' "$captured" 'release-body-from-github'

echo '--- Retries are bounded ---'
rm -f "$state/count"
exitCode=0
retry_gh flaky_gh 99 >/dev/null 2>&1 || exitCode=$?
assert 'Stops after the attempt budget.' "$(cat "$state/count")" '4'
assert 'Propagates the failing exit code.' "$exitCode" '1'

echo '--- Non-transient failures are not retried ---'
rm -f "$state/count"
exitCode=0
retry_gh fatal_gh >/dev/null 2>&1 || exitCode=$?
assert 'A non-5xx failure is attempted once.' "$(cat "$state/count")" '1'
assert 'A non-5xx failure propagates its exit code.' "$exitCode" '1'

echo '--- Failure output is replayed to the job log ---'
rm -f "$state/count"
stderrOutput=$(retry_gh flaky_gh 1 2>&1 >/dev/null)
assert 'The gh error text reaches stderr.' "$(grep -c 'HTTP 503' <<<"$stderrOutput")" '1'
assert 'The retry warning reaches stderr.' "$(grep -c '::warning::' <<<"$stderrOutput")" '1'

if (( failures > 0 )); then
  echo "gh-retry tests failed: $failures assertion(s)." >&2
  exit 1
fi

echo 'All gh-retry tests passed.'
