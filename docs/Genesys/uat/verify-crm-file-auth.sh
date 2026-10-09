#!/usr/bin/env bash
# Probe how Tiger CRM protects the document file routes ("fileUrl"), and whether
# GetCustomerDocuments + the file routes behave the way TigerCS's gateway needs.
#
# Run from a machine that can reach CRM. The secret is read from the environment and
# is never printed. Nothing here is written to CRM (GetCustomerDocuments is a read).
#
#   CRM_BASE_URL=https://tigercrm.tigergroup.ae:8014 \
#   CRM_SECRET_KEY=... \
#   CRM_CUSTOMER_ID=9001 CRM_LEAD_ID=12345 \
#   ./verify-crm-file-auth.sh [Contract|ReservationForm|RegistrationReceipt|UnitLayout]
#
# Record the output in docs/releases/UAT-Chatbot-Inactivity-And-Document-Copy.md.
set -euo pipefail

: "${CRM_BASE_URL:?set CRM_BASE_URL}" "${CRM_SECRET_KEY:?set CRM_SECRET_KEY}"
: "${CRM_CUSTOMER_ID:?set CRM_CUSTOMER_ID}" "${CRM_LEAD_ID:?set CRM_LEAD_ID}"
TYPE="${1:-Contract}"
case "$TYPE" in
  Contract) CRM_TYPE=TigerContract ;; ReservationForm) CRM_TYPE=ReservationForm ;;
  RegistrationReceipt) CRM_TYPE=RegistrationReceipt ;; UnitLayout) CRM_TYPE=Layout ;;
  *) echo "unknown type $TYPE" >&2; exit 2 ;;
esac
BASE="${CRM_BASE_URL%/}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

echo "== 1. GetCustomerDocuments ($TYPE -> $CRM_TYPE) =="
code=$(curl -sS -m 30 -o "$tmp/list.json" -w '%{http_code}' -X POST "$BASE/TicketingSystem/GetCustomerDocuments" \
  -H "X-SECRET-KEY: $CRM_SECRET_KEY" -H 'Content-Type: application/json' \
  -d "{\"CustomerID\":$CRM_CUSTOMER_ID,\"LeadID\":$CRM_LEAD_ID,\"DocumentType\":\"$CRM_TYPE\"}")
echo "status: $code"; head -c 1500 "$tmp/list.json"; echo
[ "$code" = 200 ] || { echo "cannot continue: listing did not return 200"; exit 1; }

echo "== 2. without the secret (expect 401, never a document or a login page) =="
no_secret=$(curl -sS -m 30 -o /dev/null -w '%{http_code}' -X POST "$BASE/TicketingSystem/GetCustomerDocuments" \
  -H 'Content-Type: application/json' -d "{\"CustomerID\":$CRM_CUSTOMER_ID,\"LeadID\":$CRM_LEAD_ID,\"DocumentType\":\"$CRM_TYPE\"}")
echo "status: $no_secret"

# fileUrl values, without printing the secret
mapfile -t urls < <(python3 - "$tmp/list.json" <<'PY'
import json,sys
for a in json.load(open(sys.argv[1])).get("attachments",[]): print(a.get("fileUrl",""))
PY
)
[ "${#urls[@]}" -gt 0 ] || { echo "no attachments listed for this customer/lead/type"; exit 0; }

for u in "${urls[@]}"; do
  echo "== 3. file route: $u =="
  case "$u" in http*) full="$u" ;; "~/"*) full="$BASE/${u:2}" ;; /*) full="$BASE$u" ;; *) full="$BASE/$u" ;; esac
  echo "-- WITHOUT the secret --"
  curl -sS -m 30 -o "$tmp/anon.bin" -D "$tmp/anon.h" -w 'status: %{http_code}  type: %{content_type}  bytes: %{size_download}\n' "$full" || true
  head -1 "$tmp/anon.h" | tr -d '\r'; grep -i '^location:' "$tmp/anon.h" | tr -d '\r' || true
  echo "-- WITH X-SECRET-KEY --"
  curl -sS -m 30 -o "$tmp/auth.bin" -D "$tmp/auth.h" -H "X-SECRET-KEY: $CRM_SECRET_KEY" \
    -w 'status: %{http_code}  type: %{content_type}  bytes: %{size_download}\n' "$full" || true
  grep -iE '^(content-type|content-disposition|content-length|location):' "$tmp/auth.h" | tr -d '\r' || true
  echo "first bytes (hex): $(head -c 12 "$tmp/auth.bin" | od -An -tx1 | tr -d ' \n')"
  echo "file(1): $(file -b "$tmp/auth.bin" 2>/dev/null || echo n/a)"
done

cat <<'TXT'

How to read this:
  step 2   401            -> the listing is protected by the secret (required)
  step 3   anonymous 200 + file -> the file route is PUBLIC (unguessable URL at best). TigerCS still works; tell security.
           anonymous 401/403    -> protected; good.
           anonymous 302 / HTML -> login redirect: cookie/forms auth. TigerCS cannot use this; CRM needs a route that checks X-SECRET-KEY.
  step 3   with-secret 200 + a real file (%PDF-, PNG 89504e47, JPEG ffd8ff) -> TigerCS can download it.
           with-secret 401/403/302/HTML -> TigerCS will report CRM_AUTHENTICATION_FAILED / CRM_ACCESS_DENIED / CRM_INVALID_RESPONSE.
TXT
