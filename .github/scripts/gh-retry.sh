#!/usr/bin/env bash
# Shared helper for the release jobs: run a gh command, retrying transient GitHub API 5xx
# responses (for example "HTTP 503: No server is currently available to service your request")
# with exponential backoff. Any other failure is returned immediately so real problems still
# fail the job.
#
# Usage:
#   source .github/scripts/gh-retry.sh
#   retry_gh gh release view "$RELEASE_TAG" --json body --jq .body

retry_gh() {
  local attempt=1
  local maxAttempts="${GH_RETRY_MAX_ATTEMPTS:-4}"
  local delaySeconds="${GH_RETRY_INITIAL_DELAY_SECONDS:-2}"
  local errorFile exitCode

  while true; do
    errorFile=$(mktemp)
    exitCode=0

    # stderr goes to a file rather than a `tee` process substitution so the retry decision
    # always reads the complete message instead of racing the writer, then it is replayed
    # to the job log.
    "$@" 2>"$errorFile" || exitCode=$?
    cat "$errorFile" >&2

    if (( exitCode == 0 )); then
      rm -f "$errorFile"
      return 0
    fi

    if (( attempt >= maxAttempts )) || ! grep -Eq 'HTTP 5[0-9]{2}' "$errorFile"; then
      rm -f "$errorFile"
      return "$exitCode"
    fi

    # To stderr: callers capture stdout (`notes=$(retry_gh gh release view ...)`), so a
    # warning on stdout would end up spliced into the release notes.
    echo "::warning::GitHub API returned a 5xx response; retrying in $delaySeconds seconds (attempt $attempt/$maxAttempts)." >&2
    rm -f "$errorFile"
    sleep "$delaySeconds"
    attempt=$((attempt + 1))
    delaySeconds=$((delaySeconds * 2))
  done
}
