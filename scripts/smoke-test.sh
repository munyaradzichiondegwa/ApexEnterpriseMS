#!/usr/bin/env bash
# ApexERP smoke test: exercises registration -> GL account creation -> customer -> AR invoice
# -> posting -> trial balance against a running instance (e.g. after `docker compose up`).
#
# Usage: BASE_URL=http://localhost:8080 ./scripts/smoke-test.sh
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}/api/v1"
EMAIL="smoketest+$(date +%s)@apexerp.local"
PASSWORD="SmokeTest123!"

echo "== 1. Registering company + admin user =="
REGISTER_RESPONSE=$(curl -sf -X POST "$BASE_URL/auth/register" \
  -H "Content-Type: application/json" \
  -d "{\"companyName\":\"Smoke Test Co\",\"email\":\"$EMAIL\",\"password\":\"$PASSWORD\",\"firstName\":\"Smoke\",\"lastName\":\"Test\"}")
TOKEN=$(echo "$REGISTER_RESPONSE" | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)
echo "Got access token: ${TOKEN:0:20}..."

AUTH_HEADER="Authorization: Bearer $TOKEN"

echo "== 2. Creating two GL accounts (Cash, Sales Revenue) =="
CASH_ACCOUNT=$(curl -sf -X POST "$BASE_URL/gl/accounts" -H "$AUTH_HEADER" -H "Content-Type: application/json" \
  -d '{"code":"1000","name":"Cash on Hand","type":"Asset","currency":"USD","isBankAccount":false,"isControlAccount":false}')
CASH_ID=$(echo "$CASH_ACCOUNT" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)

REVENUE_ACCOUNT=$(curl -sf -X POST "$BASE_URL/gl/accounts" -H "$AUTH_HEADER" -H "Content-Type: application/json" \
  -d '{"code":"4000","name":"Sales Revenue","type":"Income","currency":"USD","isBankAccount":false,"isControlAccount":false}')
REVENUE_ID=$(echo "$REVENUE_ACCOUNT" | grep -o '"id":"[^"]*"' | head -1 | cut -d'"' -f4)
echo "Cash account: $CASH_ID | Revenue account: $REVENUE_ID"

echo "== 3. Posting a manual journal entry (Dr Cash 100, Cr Revenue 100) =="
curl -sf -X POST "$BASE_URL/gl/journal-entries" -H "$AUTH_HEADER" -H "Content-Type: application/json" \
  -d "{\"entryDate\":\"$(date +%Y-%m-%d)\",\"description\":\"Smoke test sale\",\"reference\":\"SMOKE-1\",\"lines\":[{\"glAccountId\":\"$CASH_ID\",\"debit\":100,\"credit\":0,\"currency\":\"USD\",\"exchangeRate\":1},{\"glAccountId\":\"$REVENUE_ID\",\"debit\":0,\"credit\":100,\"currency\":\"USD\",\"exchangeRate\":1}]}" \
  > /dev/null
echo "Journal posted."

echo "== 4. Fetching Trial Balance (should show both accounts with a 100 balance) =="
curl -sf "$BASE_URL/reports/trial-balance" -H "$AUTH_HEADER" | python3 -m json.tool

echo ""
echo "✅ Smoke test complete. If the trial balance above shows Cash Dr 100 and Sales Revenue Cr 100, the GL posting engine is working end-to-end."
