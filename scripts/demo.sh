#!/usr/bin/env bash
# End-to-end demonstration against a running LedgerCore (docker compose up, or `dotnet run`).
# Every step asserts the expected HTTP status, so this doubles as the CI smoke test.
#
#   ./scripts/demo.sh                                   # API at http://localhost:8080, second instance at :8081
#   API_1=http://localhost:5080 API_2=http://localhost:5080 ./scripts/demo.sh   # single local instance
#
# Requires: bash, curl. Works on Linux, macOS and Git Bash on Windows.
set -euo pipefail

API_1="${API_1:-http://localhost:8080}"
API_2="${API_2:-http://localhost:8081}"
ALICE_WALLET="10000000-0000-4000-a000-00000000a11c"
BOB_WALLET="10000000-0000-4000-a000-000000000b0b"
FAILURES=0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

new_uuid() {
  if command -v uuidgen >/dev/null 2>&1; then uuidgen | tr 'A-Z' 'a-z'
  elif [ -r /proc/sys/kernel/random/uuid ]; then cat /proc/sys/kernel/random/uuid
  else printf '%04x%04x-%04x-4%03x-a%03x-%04x%04x%04x\n' $RANDOM $RANDOM $RANDOM $((RANDOM % 4096)) $((RANDOM % 4096)) $RANDOM $RANDOM $RANDOM
  fi
}

json_field() { sed -n "s/.*\"$1\":\"\\{0,1\\}\\([^\",}]*\\).*/\\1/p" | head -n 1; }

# call <expected-status> <description> <curl args...>; body is left in $WORK/body
call() {
  local expected="$1" description="$2"; shift 2
  local status
  status="$(curl -s -o "$WORK/body" -w '%{http_code}' "$@")"
  if [ "$status" = "$expected" ]; then
    printf '  [ok]   %-62s %s\n' "$description" "$status"
  else
    printf '  [FAIL] %-62s expected %s, got %s\n         %s\n' "$description" "$expected" "$status" "$(head -c 400 "$WORK/body")"
    FAILURES=$((FAILURES + 1))
  fi
}

token() { # token <base> <subject> <role> <name>
  curl -s -X POST "$1/api/dev/tokens" -H 'Content-Type: application/json' \
    -d "{\"subject\":\"$2\",\"name\":\"$4\",\"roles\":[\"$3\"]}" | json_field accessToken
}

transfer() { # transfer <expected> <description> <base> <token> <from> <to> <amount> <key> [extra curl args]
  local expected="$1" description="$2" base="$3" tok="$4" from="$5" to="$6" amount="$7" key="$8"; shift 8
  call "$expected" "$description" -X POST "$base/api/transfers" \
    -H "Authorization: Bearer $tok" -H 'Content-Type: application/json' -H "Idempotency-Key: $key" "$@" \
    -d "{\"fromWalletId\":\"$from\",\"toWalletId\":\"$to\",\"amountMinor\":$amount,\"currency\":\"NOK\"}"
}

echo "Waiting for $API_1 and $API_2 to become ready..."
for base in "$API_1" "$API_2"; do
  for _ in $(seq 1 120); do
    if [ "$(curl -s -o /dev/null -w '%{http_code}' "$base/health/ready")" = "200" ]; then break; fi
    sleep 1
  done
done

ALICE="$(token "$API_1" 00000000-0000-4000-a000-00000000a11c customer Alice)"
BOB="$(token "$API_1" 00000000-0000-4000-a000-000000000b0b customer Bob)"
OPERATOR="$(token "$API_1" demo-operator ledger-operator Operator)"
RUN="$(new_uuid | cut -c1-8)"

echo "1. Basics"
call 200 "Alice reads her wallet" "$API_1/api/wallets/$ALICE_WALLET/balance" -H "Authorization: Bearer $ALICE"
call 401 "Anonymous request is rejected" "$API_1/api/wallets"
transfer 201 "Alice pays Bob 25.00 NOK" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 2500 "demo-$RUN-pay"
transfer 201 "Same request, same key -> replayed, not re-executed" "$API_2" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 2500 "demo-$RUN-pay"
grep -q '"transferId"' "$WORK/body" || { echo "  [FAIL] replay body has no transferId"; FAILURES=$((FAILURES + 1)); }

echo "2. Attempts to break it"
transfer 422 "Challenge 04: same key, different amount" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 9999 "demo-$RUN-pay"
transfer 422 "Challenge 01: overdraw 1 000 000 000.00 NOK" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 100000000000 "demo-$RUN-overdraw"
transfer 403 "Challenge 07: Bob debits Alice's wallet" "$API_1" "$BOB" "$ALICE_WALLET" "$BOB_WALLET" 100 "demo-$RUN-steal"
transfer 400 "Zero amount" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 0 "demo-$RUN-zero"
transfer 400 "Self transfer" "$API_1" "$ALICE" "$ALICE_WALLET" "$ALICE_WALLET" 100 "demo-$RUN-self"
transfer 500 "Challenge 05: injected failure before COMMIT" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 100 "demo-$RUN-fail" \
  -H 'X-LedgerCore-Fail-At: BeforeCommit'
transfer 201 "  ...retry with the same key executes once" "$API_1" "$ALICE" "$ALICE_WALLET" "$BOB_WALLET" 100 "demo-$RUN-fail"

echo "3. Challenge 02/13: two instances race to spend the same 1 000 NOK"
CUSTOMER="$(new_uuid)"
CUSTOMER_TOKEN="$(token "$API_1" "$CUSTOMER" customer Racer)"
call 201 "Create racer wallet" -X POST "$API_1/api/wallets" -H "Authorization: Bearer $CUSTOMER_TOKEN" \
  -H 'Content-Type: application/json' -d '{"name":"Race","currency":"NOK"}'
RACE_WALLET="$(json_field id < "$WORK/body")"
call 201 "Fund racer wallet with 1 000.00 NOK" -X POST "$API_1/api/dev/funding" -H "Authorization: Bearer $OPERATOR" \
  -H 'Content-Type: application/json' -H "Idempotency-Key: demo-$RUN-fund" \
  -d "{\"walletId\":\"$RACE_WALLET\",\"amountMinor\":100000,\"currency\":\"NOK\"}"
for i in 1 2; do
  base="$API_1"; [ "$i" = 2 ] && base="$API_2"
  curl -s -o /dev/null -w '%{http_code}\n' -X POST "$base/api/transfers" -H "Authorization: Bearer $CUSTOMER_TOKEN" \
    -H 'Content-Type: application/json' -H "Idempotency-Key: demo-$RUN-race-$i" \
    -d "{\"fromWalletId\":\"$RACE_WALLET\",\"toWalletId\":\"$BOB_WALLET\",\"amountMinor\":80000,\"currency\":\"NOK\"}" >"$WORK/race-$i" &
done
wait
WON="$(cat "$WORK"/race-* | tr -d '\r' | grep -c '^201$' || true)"
LOST="$(cat "$WORK"/race-* | tr -d '\r' | grep -c '^422$' || true)"
if [ "$WON" = "1" ] && [ "$LOST" = "1" ]; then
  echo "  [ok]   exactly one 800 NOK transfer succeeded, the other got 422"
else
  echo "  [FAIL] race statuses: $(cat "$WORK"/race-* | tr '\n' ' ')"; FAILURES=$((FAILURES + 1))
fi
call 200 "Racer balance" "$API_2/api/wallets/$RACE_WALLET/balance" -H "Authorization: Bearer $CUSTOMER_TOKEN"
grep -q '"availableBalanceMinor":20000' "$WORK/body" && echo "  [ok]   200.00 NOK left" || { echo "  [FAIL] $(cat "$WORK/body")"; FAILURES=$((FAILURES + 1)); }

echo "4. Challenge 03: 100 identical requests in parallel, alternating instances"
seq 1 100 | xargs -P 50 -I{} sh -c '
  base="$0"; [ $(( {} % 2 )) -eq 0 ] && base="$1"
  curl -s -o /dev/null -w "%{http_code}\n" -X POST "$base/api/transfers" -H "Authorization: Bearer $2" \
    -H "Content-Type: application/json" -H "Idempotency-Key: $3" \
    -d "{\"fromWalletId\":\"$4\",\"toWalletId\":\"$5\",\"amountMinor\":100,\"currency\":\"NOK\"}"
' "$API_1" "$API_2" "$ALICE" "demo-$RUN-x100" "$ALICE_WALLET" "$BOB_WALLET" >"$WORK/dups"
CREATED="$(grep -c '^201$' "$WORK/dups" || true)"
[ "$CREATED" = "100" ] && echo "  [ok]   all 100 answered 201 with the same transfer" || { echo "  [FAIL] only $CREATED x 201"; FAILURES=$((FAILURES + 1)); }

echo "5. Challenge 10: rebuild balances from the journal"
call 200 "Reconciliation" -X POST "$API_1/api/admin/reconciliation?freezeAffectedWallets=false" -H "Authorization: Bearer $OPERATOR"
grep -q '"isConsistent":true' "$WORK/body" && echo "  [ok]   journal and cached balances agree" || { echo "  [FAIL] $(cat "$WORK/body")"; FAILURES=$((FAILURES + 1)); }

echo
if [ "$FAILURES" -eq 0 ]; then echo "All demo checks passed."; else echo "$FAILURES demo check(s) FAILED."; exit 1; fi
