#!/usr/bin/env bash
# Drives the composed stack over HTTP. Every code carries a run id, so a kept Postgres volume never collides.
# Needs: curl, python3. The stack must be up: docker compose up --build --wait
set -euo pipefail

API="${API:-http://127.0.0.1:5220}"
RABBIT="${RABBIT:-http://127.0.0.1:15710}"
RUN="$(date +%s)$((RANDOM % 900 + 100))"
JSON='content-type: application/json'

say() { printf '  %s\n' "$*"; }
fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }
jget() { python3 -c "import sys,json; d=json.load(sys.stdin); print(d$1)"; }

open_campaign() { # code usesTotal -> prints nothing
  curl -sf -X POST "$API/api/campaigns" -H "$JSON" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Campaign\",\"channel\":\"Email\",\"market\":\"US\",\"usesTotal\":$2,\"discountCents\":1000}" >/dev/null \
    || fail "could not open campaign $1"
}
book() { # code reference uses -> prints redemption id
  curl -sf -X POST "$API/api/redemptions" -H "$JSON" \
    -d "{\"campaignCode\":\"$1\",\"reference\":\"$2\",\"uses\":$3}" | jget "['id']"
}
redemption_field() { curl -sf "$API/api/redemptions/$1" | jget "['$2']"; }
remaining() { curl -sf "$API/api/campaigns/$1" | jget "['usesLeft']"; }

wait_for() { # description command... : polls up to 30s until the command succeeds
  local what="$1"; shift
  for _ in $(seq 1 60); do
    if "$@" >/dev/null 2>&1; then return 0; fi
    sleep 0.5
  done
  fail "timed out waiting for $what"
}
status_is() { [ "$(redemption_field "$1" status)" = "$2" ]; }
remaining_is() { [ "$(remaining "$1")" = "$2" ]; }
terminal() { case "$(redemption_field "$1" status)" in Redeemed|Rejected|Revoked) return 0 ;; *) return 1 ;; esac; }
event_types() { curl -sf "$API/api/redemptions/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/coupons.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "1. input is validated at the door"
open_campaign "V-$RUN" 10
for body in \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"\",\"uses\":1}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":0}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":4}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":4294967297}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":9223372036854775807}"; do
  code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/redemptions" -H "$JSON" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing use counts are all 400"

echo "2. happy path"
open_campaign "H-$RUN" 50
ID="$(book "H-$RUN" "CUS-$RUN-1" 3)"
wait_for "redemption to be Redeemed" status_is "$ID" Redeemed
[ "$(redemption_field "$ID" valueCents)" = 3000 ] || fail "value should be 3 uses x 1000 = 3000"
[ "$(curl -sf "$API/api/coupons/$ID" | jget "['status']")" = Issued ] || fail "coupon not issued"
wait_for "remaining uses to be 47" remaining_is "H-$RUN" 47
TYPES="$(event_types "$ID")"
for t in uses.reserve uses.held coupon.issued; do
  case "$TYPES" in *"$t"*) ;; *) fail "Kafka log is missing $t (has: $TYPES)" ;; esac
done
say "redeemed, value 3000, remaining 47, Kafka log has the three steps"

echo "3. last use: three redemptions, one commits, two are short and write no hold"
open_campaign "L-$RUN" 1
TMP="$(mktemp -d)"
for n in A B C; do ( book "L-$RUN" "CUS-$RUN-$n" 1 >"$TMP/$n" ) & done
wait
IDS=""
for n in A B C; do [ -s "$TMP/$n" ] || fail "redemption $n was not accepted"; IDS="$IDS $(cat "$TMP/$n")"; done
rm -rf "$TMP"
for i in $IDS; do wait_for "redemption $i to settle" terminal "$i"; done
REDEEMED=0; SHORT=0
for i in $IDS; do
  s="$(redemption_field "$i" status)"
  if [ "$s" = Redeemed ]; then REDEEMED=$((REDEEMED + 1)); fi
  if [ "$s" = Rejected ]; then
    case "$(redemption_field "$i" reason)" in Short*) SHORT=$((SHORT + 1)); case "$(event_types "$i")" in *uses.held*) fail "the short loser has a hold" ;; esac ;; esac
  fi
done
[ "$REDEEMED" = 1 ] && [ "$SHORT" = 2 ] || fail "expected 1 redeemed and 2 short, got $REDEEMED redeemed and $SHORT short"
[ "$(remaining "L-$RUN")" = 0 ] || fail "remaining should be 0"
say "1 redeemed, 2 short, remaining 0, no hold for the losers"

echo "4. poison: committed as rejected, dead-lettered, the uses come back"
open_campaign "P-$RUN" 5
BEFORE="$(dlq_count)"
IDP="$(book "P-$RUN" "CUS-POISON-$RUN" 2)"
wait_for "poison redemption to be Rejected" status_is "$IDP" Rejected
wait_for "uses to be restored to 5" remaining_is "P-$RUN" 5
wait_for "coupons.work.dlq to gain one message" dlq_is $((BEFORE + 1))
[ "$(curl -sf "$API/api/coupons/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the coupon row"
[ "$(redemption_field "$IDP" usesReleasedAt)" != None ] || fail "redemption should record the release"
say "rejected, dead-lettered (+1 in coupons.work.dlq), remaining back to 5"

echo "5. revocation races the final mark: the revocation wins, the issued coupon stays"
open_campaign "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "CUS-$RUN-R$attempt" 2)"
  # The coupon exists before the redemption is marked Redeemed (the final mark is delayed in compose).
  wait_for "coupon to exist" curl -sf "$API/api/coupons/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (redemption already Redeemed); the revocation still has to hold; retrying"
  curl -sf -X POST "$API/api/redemptions/$IDR/revocation" >/dev/null || true
  wait_for "redemption to be Revoked" status_is "$IDR" Revoked
done
[ "$WON" = 1 ] || fail "never caught the redemption between coupon issued and final mark"
curl -sf -X POST "$API/api/redemptions/$IDR/revocation" >/dev/null || fail "revocation was refused"
wait_for "redemption to be Revoked" status_is "$IDR" Revoked
sleep 3 # longer than the final-mark delay: the late mark must not undo the revocation
status_is "$IDR" Revoked || fail "the final mark overwrote the revocation"
[ "$(curl -sf "$API/api/coupons/$IDR" | jget "['status']")" = Issued ] || fail "the issued coupon was rolled back"
[ "$(redemption_field "$IDR" couponNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "uses to come back" remaining_is "R-$RUN" 6
code="$(curl -s -o /dev/null -w '%{http_code}' -X POST "$API/api/redemptions/$IDR/revocation")"
[ "$code" = 409 ] || fail "second revocation should be 409, got $code"
say "redemption Revoked, coupon still Issued, remaining 6, second revocation 409"

echo "promo smoke passed"
