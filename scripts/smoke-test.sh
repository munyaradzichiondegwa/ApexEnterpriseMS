#!/usr/bin/env bash
# ApexERP smoke test. Exercises, against a running instance (e.g. after `docker compose up`):
#   registration -> chart of accounts -> manual journal lifecycle (draft -> submit -> approve -> post)
#   -> idempotent posting -> trial balance + integrity check -> single reversal
#   -> role-based access control (a read-only user cannot post, close periods or create roles).
#
# The journal workflow needs one user to prepare AND approve, so run it against a Development API
# (Controls:JournalApproval:AllowSelfApproval=true, the Development default). In production that is off
# on purpose (segregation of duties) and the approval step below would be refused.
#
# Usage: BASE_URL=http://localhost:8080 ./scripts/smoke-test.sh
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}/api/v1"
STAMP="$(date +%s)"
EMAIL="smoketest+$STAMP@apexerp.local"
PASSWORD="SmokeTest123!"
READER_EMAIL="reader+$STAMP@apexerp.local"
JSON="Content-Type: application/json"

fail() { echo "❌ $*" >&2; exit 1; }
json() { python3 -c "import sys,json; d=json.load(sys.stdin); print(eval(sys.argv[1]))" "$1"; }
call() { # METHOD PATH [BODY] -> body on stdout; aborts on any non-2xx
  local method="$1" path="$2" body="${3:-}"
  if [ -n "$body" ]; then curl -sf -X "$method" "$BASE_URL$path" -H "$AUTH" -H "$JSON" -d "$body"; else curl -sf -X "$method" "$BASE_URL$path" -H "$AUTH"; fi
}
status() { # METHOD PATH [BODY] [AUTH_HEADER] -> HTTP status code only
  local method="$1" path="$2" body="${3:-}" auth="${4:-$AUTH}"
  curl -s -o /dev/null -w "%{http_code}" -X "$method" "$BASE_URL$path" -H "$auth" -H "$JSON" ${body:+-d "$body"}
}
expect() { [ "$2" = "$3" ] && echo "   ✓ $1" || fail "$1 (expected $3, got $2)"; }

echo "== 1. Registering company + admin user =="
REGISTER=$(curl -sf -X POST "$BASE_URL/auth/register" -H "$JSON" \
  -d "{\"companyName\":\"Smoke Test Co\",\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",\"firstName\":\"Smoke\",\"lastName\":\"Test\"}")
TOKEN=$(echo "$REGISTER" | json "d['accessToken']")
AUTH="Authorization: Bearer $TOKEN"
echo "$REGISTER" | json "'   permissions granted to the first user: ' + str(d['permissions'])"

echo "== 2. Unauthenticated requests are refused =="
expect "GET /gl/accounts without a token -> 401" "$(curl -s -o /dev/null -w '%{http_code}' "$BASE_URL/gl/accounts")" 401

echo "== 3. Creating two GL accounts (Cash, Sales Revenue) =="
CASH_ID=$(call POST /gl/accounts '{"code":"1000","name":"Cash on Hand","type":"Asset","currency":"USD","isBankAccount":false,"isControlAccount":false}' | json "d['id']")
REVENUE_ID=$(call POST /gl/accounts '{"code":"4000","name":"Sales Revenue","type":"Income","currency":"USD","isBankAccount":false,"isControlAccount":false}' | json "d['id']")
echo "   Cash $CASH_ID | Revenue $REVENUE_ID"

echo "== 4. Manual journal lifecycle (Dr Cash 100, Cr Revenue 100) =="
TODAY="$(date +%Y-%m-%d)"
DRAFT=$(call POST /gl/journal-entries "{\"entryDate\":\"$TODAY\",\"description\":\"Smoke test sale\",\"reference\":\"SMOKE-1\",\"lines\":[{\"glAccountId\":\"$CASH_ID\",\"debit\":100,\"credit\":0,\"currency\":\"USD\",\"exchangeRate\":1},{\"glAccountId\":\"$REVENUE_ID\",\"debit\":0,\"credit\":100,\"currency\":\"USD\",\"exchangeRate\":1}]}")
JE_ID=$(echo "$DRAFT" | json "d['id']")
expect "a new manual journal is a Draft" "$(echo "$DRAFT" | json "d['status']")" Draft
BEFORE=$(call GET "/reports/trial-balance" | json "len(d['lines'])")
expect "a draft does not touch the ledger (trial balance still empty)" "$BEFORE" 0
expect "an unbalanced journal is refused" "$(status POST /gl/journal-entries "{\"entryDate\":\"$TODAY\",\"lines\":[{\"glAccountId\":\"$CASH_ID\",\"debit\":100,\"credit\":0,\"currency\":\"USD\",\"exchangeRate\":1},{\"glAccountId\":\"$REVENUE_ID\",\"debit\":0,\"credit\":90,\"currency\":\"USD\",\"exchangeRate\":1}]}")" 400
expect "a zero exchange rate is refused" "$(status POST /gl/journal-entries "{\"entryDate\":\"$TODAY\",\"lines\":[{\"glAccountId\":\"$CASH_ID\",\"debit\":100,\"credit\":0,\"currency\":\"USD\",\"exchangeRate\":0},{\"glAccountId\":\"$REVENUE_ID\",\"debit\":0,\"credit\":100,\"currency\":\"USD\",\"exchangeRate\":0}]}")" 400
expect "posting before approval is refused" "$(status POST "/gl/journal-entries/$JE_ID/post")" 400
expect "submit"  "$(call POST "/gl/journal-entries/$JE_ID/submit"  | json "d['status']")" Submitted
expect "approve" "$(call POST "/gl/journal-entries/$JE_ID/approve" '{}' | json "d['status']")" Approved
POSTED=$(call POST "/gl/journal-entries/$JE_ID/post")
expect "post"    "$(echo "$POSTED" | json "d['status']")" Posted
echo "   journal number: $(echo "$POSTED" | json "d['entryNo']")"
expect "posting again is a harmless no-op (no duplicate)" "$(call POST "/gl/journal-entries/$JE_ID/post" | json "d['entryNo']")" "$(echo "$POSTED" | json "d['entryNo']")"

echo "== 5. Trial balance and integrity check =="
call GET "/reports/trial-balance" | python3 -m json.tool | sed 's/^/   /' | head -30
expect "trial balance is balanced" "$(call GET /reports/trial-balance | json "d['isBalanced']")" True
expect "integrity check: no unbalanced journals" "$(call GET /reports/integrity-check | json "d['unbalancedPostedJournals']")" 0

echo "== 6. Reversal: allowed once, and neutralises the original =="
REVERSAL=$(call POST "/gl/journal-entries/$JE_ID/reverse" '"Smoke test reversal"')
echo "   reversal journal: $(echo "$REVERSAL" | json "d['entryNo']")"
expect "reversing the same journal again is refused" "$(status POST "/gl/journal-entries/$JE_ID/reverse" '"again"')" 400
expect "trial balance still balanced after reversal" "$(call GET /reports/trial-balance | json "d['isBalanced']")" True
expect "cash nets to zero after the reversal (original is not dropped from reports)" \
  "$(call GET /reports/trial-balance | json "sum(l['debit'] - l['credit'] for l in d['lines'] if l['code'] == '1000')")" 0

echo "== 7. Role-based access control =="
ROLE_ID=$(call POST /admin/roles '{"name":"Read only","permissionsJson":"[\"gl.accounts.read\",\"gl.journals.read\",\"reports.financial.read\"]"}' | json "d['id']")
call POST /admin/users "{\"email\":\"$READER_EMAIL\",\"password\":\"$PASSWORD\",\"roleId\":\"$ROLE_ID\",\"firstName\":\"Read\",\"lastName\":\"Only\"}" > /dev/null
READER_TOKEN=$(curl -sf -X POST "$BASE_URL/auth/login" -H "$JSON" -d "{\"email\":\"$READER_EMAIL\",\"password\":\"$PASSWORD\"}" | json "d['accessToken']")
READER="Authorization: Bearer $READER_TOKEN"
expect "read-only user can read accounts"            "$(status GET /gl/accounts '' "$READER")" 200
expect "read-only user can read the trial balance"   "$(status GET /reports/trial-balance '' "$READER")" 200
expect "read-only user cannot create a journal"      "$(status POST /gl/journal-entries '{}' "$READER")" 403
expect "read-only user cannot post a journal"        "$(status POST "/gl/journal-entries/$JE_ID/post" '' "$READER")" 403
expect "read-only user cannot reverse a journal"     "$(status POST "/gl/journal-entries/$JE_ID/reverse" '"x"' "$READER")" 403
expect "read-only user cannot close a period"        "$(status POST "/gl/periods/$(call GET /gl/periods | json "d[0]['id']")/close" '' "$READER")" 403
expect "read-only user cannot create a role"         "$(status POST /admin/roles '{"name":"Hacker","permissionsJson":"[\"*\"]"}' "$READER")" 403
expect "read-only user cannot create a user"         "$(status POST /admin/users '{}' "$READER")" 403
expect "read-only user cannot read payroll"          "$(status GET /payroll/employees '' "$READER")" 403

echo ""
echo "✅ Smoke test complete: the journal lifecycle, ledger integrity checks and access control all behave as expected."
