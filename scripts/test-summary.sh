#!/usr/bin/env bash
# Prints the pass/fail counts of every TRX file in a directory and fails if a suite failed or ran no tests.
# On GitHub Actions the counts are also written as notice annotations and to the job summary.
#
#   ./scripts/test-summary.sh TestResults
set -euo pipefail

DIR="${1:-TestResults}"
shopt -s nullglob
files=("$DIR"/*.trx)
if [ "${#files[@]}" -eq 0 ]; then
  echo "::error title=Test summary::No TRX files in $DIR"
  exit 1
fi

attr() { sed -n "s/.* $1=\"\([0-9]*\)\".*/\1/p" <<<"$2"; }

status=0
[ -n "${GITHUB_STEP_SUMMARY:-}" ] && printf '| Suite | Total | Passed | Failed | Not executed |\n|---|---|---|---|---|\n' >>"$GITHUB_STEP_SUMMARY"
for trx in "${files[@]}"; do
  suite="$(basename "$trx" .trx)"
  counters="$(grep -o '<Counters [^>]*>' "$trx" | head -n 1)"
  total="$(attr total "$counters")"; passed="$(attr passed "$counters")"
  failed="$(attr failed "$counters")"; not_executed="$(attr notExecuted "$counters")"
  line="$suite: $passed passed, $failed failed, $not_executed not executed ($total total)"
  if [ "${total:-0}" -eq 0 ] || [ "${failed:-0}" -ne 0 ]; then
    echo "::error title=Test summary::$line"
    status=1
  else
    echo "::notice title=Test summary::$line"
  fi
  [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "| $suite | $total | $passed | $failed | $not_executed |" >>"$GITHUB_STEP_SUMMARY"
done
exit "$status"
