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

ADMIN_EMAIL="${ADMIN_EMAIL:-admin@promo.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-PromoAdmin2026}"
CUSTOMER_PASSWORD="Smoke-Passw0rd1"
code_of() { curl -s -o /dev/null -w '%{http_code}' "$@"; }
token_of() { python3 -c "import sys,json; print(json.load(sys.stdin)['accessToken'])"; }

open_campaign() { # code usesTotal -> prints nothing
  curl -sf -X POST "$API/api/campaigns" -H "$JSON" -H "$ADM" \
    -d "{\"code\":\"$1\",\"name\":\"Smoke Campaign\",\"channel\":\"Email\",\"market\":\"US\",\"usesTotal\":$2,\"discountCents\":1000}" >/dev/null \
    || fail "could not open campaign $1"
}
book() { # code reference uses -> prints redemption id
  curl -sf -X POST "$API/api/redemptions" -H "$JSON" -H "$CUS" \
    -d "{\"campaignCode\":\"$1\",\"reference\":\"$2\",\"uses\":$3}" | jget "['id']"
}
redemption_field() { curl -sf -H "$ADM" "$API/api/redemptions/$1" | jget "['$2']"; }
remaining() { curl -sf -H "$ADM" "$API/api/campaigns/$1" | jget "['usesLeft']"; }

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
event_types() { curl -sf -H "$ADM" "$API/api/redemptions/$1/events" | python3 -c "import sys,json; print(' '.join(e['type'] for e in json.load(sys.stdin)))"; }
dlq_is() { [ "$(dlq_count)" = "$1" ]; }
dlq_count() { curl -sf -u platform:platform "$RABBIT/api/queues/%2F/coupons.work.dlq" | jget "['messages']"; }

curl -sf "$API/api/health" >/dev/null || fail "API is not healthy at $API; run: docker compose up --build --wait"

echo "0. authentication and ownership"
[ "$(code_of "$API/api/campaigns")" = 401 ] || fail "an anonymous request should be 401"
[ "$(code_of -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"wrong-Passw0rd\"}")" = 401 ] || fail "a wrong password should be 401"
ADM="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}" | token_of)" || fail "the administrator could not sign in"
CUS="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"smoke-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)" || fail "could not register a customer"
OTHER="Authorization: Bearer $(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"other-$RUN@example.com\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)"
[ "$(code_of -X POST "$API/api/campaigns" -H "$JSON" -H "$CUS" -d '{"code":"X"}')" = 403 ] || fail "a customer must not open campaigns"
open_campaign "A-$RUN" 5
IDA="$(book "A-$RUN" "CUS-$RUN-OWN" 1)"
[ "$(code_of -H "$OTHER" "$API/api/redemptions/$IDA")" = 404 ] || fail "another customer must get 404 for someone else's redemption"
[ "$(code_of -H "$CUS" "$API/api/redemptions/$IDA/events")" = 403 ] || fail "the event log is staff only"
[ "$(code_of -X POST -H "$OTHER" "$API/api/redemptions/$IDA/revocation")" = 404 ] || fail "another customer must not revoke it"
say "401 anonymous, 401 bad password, 403 customer opening a campaign, 404 for another customer, 403 on the event log"

echo "0b. password reset ends the old sessions (needs the dev mailbox the demo stack turns on)"
RESET_EMAIL="reset-$RUN@example.com"
OLD="$(curl -sf -X POST "$API/api/auth/register" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\",\"password\":\"$CUSTOMER_PASSWORD\"}" | token_of)"
[ "$(code_of -H "Authorization: Bearer $OLD" "$API/api/auth/me")" = 200 ] || fail "the new session should work"
[ "$(code_of -X POST "$API/api/auth/forgot-password" -H "$JSON" -d "{\"email\":\"nobody-$RUN@example.com\"}")" = 202 ] || fail "an unknown address must look the same"
curl -sf -X POST "$API/api/auth/forgot-password" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\"}" >/dev/null || fail "forgot-password failed"
LINK="$(curl -sf "$API/api/dev/mailbox?to=$RESET_EMAIL" | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['link'])")"
link_param() { python3 -c "import sys,urllib.parse as u; print(u.parse_qs(u.urlparse(sys.argv[1]).query)['$1'][0])" "$LINK"; }
[ "$(code_of -X POST "$API/api/auth/reset-password" -H "$JSON" -d "{\"userId\":\"$(link_param reset)\",\"token\":\"$(link_param token)\",\"newPassword\":\"Another-Passw0rd9\"}")" = 204 ] || fail "reset failed"
sleep 3 # the session version is cached for a moment
[ "$(code_of -H "Authorization: Bearer $OLD" "$API/api/auth/me")" = 401 ] || fail "the old access token should be revoked"
[ "$(code_of -X POST "$API/api/auth/login" -H "$JSON" -d "{\"email\":\"$RESET_EMAIL\",\"password\":\"Another-Passw0rd9\"}")" = 200 ] || fail "the new password should work"
say "reset link works once, the old access token is revoked, the new password signs in"

echo "1. input is validated at the door"
open_campaign "V-$RUN" 10
for body in \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"\",\"uses\":1}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":0}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":4}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":4294967297}" \
  "{\"campaignCode\":\"V-$RUN\",\"reference\":\"R-$RUN\",\"uses\":9223372036854775807}"; do
  code="$(code_of -X POST "$API/api/redemptions" -H "$JSON" -H "$CUS" -d "$body")"
  [ "$code" = 400 ] || fail "expected 400 for $body, got $code"
done
say "empty, zero, over-limit and overflowing use counts are all 400"

echo "2. happy path"
open_campaign "H-$RUN" 50
ID="$(book "H-$RUN" "CUS-$RUN-1" 3)"
wait_for "redemption to be Redeemed" status_is "$ID" Redeemed
[ "$(redemption_field "$ID" valueCents)" = 3000 ] || fail "value should be 3 uses x 1000 = 3000"
[ "$(curl -sf -H "$ADM" "$API/api/coupons/$ID" | jget "['status']")" = Issued ] || fail "coupon not issued"
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
[ "$(curl -sf -H "$ADM" "$API/api/coupons/$IDP" | jget "['status']")" = Rejected ] || fail "rejection should be committed on the coupon row"
[ "$(redemption_field "$IDP" usesReleasedAt)" != None ] || fail "redemption should record the release"
say "rejected, dead-lettered (+1 in coupons.work.dlq), remaining back to 5"

echo "4b. dead letters can be listed by staff only, and replayed"
[ "$(code_of -H "$CUS" "$API/api/admin/dead-letters")" = 403 ] || fail "dead letters are staff only"
curl -sf -H "$ADM" "$API/api/admin/dead-letters" | python3 -c "import sys,json; q={x['queue']:x for x in json.load(sys.stdin)}; assert q['coupons.work']['count']>=1, q" || fail "the dead-letter queue should list the poison message"
BEFORE_REPLAY="$(dlq_count)"
REPLAYED="$(curl -sf -X POST -H "$ADM" "$API/api/admin/dead-letters/coupons.work/replay?max=100" | jget "['replayed']")" || fail "replay failed"
[ "$REPLAYED" -ge 1 ] || fail "replay should have taken at least one message"
# The poison reference still fails for the same reason, so it is retried and dead-lettered again: replay is for fixed causes.
wait_for "the replayed poison message to be dead-lettered again" dlq_is "$BEFORE_REPLAY"
say "listed, replayed $REPLAYED, dead-lettered again because the cause is still there"

echo "5. revocation races the final mark: the revocation wins, the issued coupon stays"
open_campaign "R-$RUN" 6
WON=0
for attempt in 1 2 3; do
  IDR="$(book "R-$RUN" "CUS-$RUN-R$attempt" 2)"
  # The coupon exists before the redemption is marked Redeemed (the final mark is delayed in compose).
  wait_for "coupon to exist" curl -sf -H "$ADM" "$API/api/coupons/$IDR"
  if status_is "$IDR" Held; then WON=1; break; fi
  say "window missed on attempt $attempt (redemption already Redeemed); the revocation still has to hold; retrying"
  curl -sf -X POST -H "$CUS" "$API/api/redemptions/$IDR/revocation" >/dev/null || true
  wait_for "redemption to be Revoked" status_is "$IDR" Revoked
done
[ "$WON" = 1 ] || fail "never caught the redemption between coupon issued and final mark"
curl -sf -X POST -H "$CUS" "$API/api/redemptions/$IDR/revocation" >/dev/null || fail "revocation was refused"
wait_for "redemption to be Revoked" status_is "$IDR" Revoked
sleep 3 # longer than the final-mark delay: the late mark must not undo the revocation
status_is "$IDR" Revoked || fail "the final mark overwrote the revocation"
[ "$(curl -sf -H "$ADM" "$API/api/coupons/$IDR" | jget "['status']")" = Issued ] || fail "the issued coupon was rolled back"
[ "$(redemption_field "$IDR" couponNumber)" = None ] || fail "the late final mark should have been skipped"
wait_for "uses to come back" remaining_is "R-$RUN" 6
code="$(code_of -X POST -H "$CUS" "$API/api/redemptions/$IDR/revocation")"
[ "$code" = 409 ] || fail "second revocation should be 409, got $code"
say "redemption Revoked, coupon still Issued, remaining 6, second revocation 409"

echo "promo smoke passed"
