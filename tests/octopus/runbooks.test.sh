#!/usr/bin/env bash
# Shell test of the Octopus runbook scripts (deploy/octopus/scripts/*.sh) with a fake docker and a fake curl on the PATH. No network, no
# Docker, no Octopus: the scripts read their variables from env (Fworker.Name -> FWORKER_NAME). Secrets are composed at run time.
#   bash tests/octopus/runbooks.test.sh      exit 0 when every check passes
set -u
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../.." && pwd); scripts=$root/deploy/octopus/scripts
tmp=$(mktemp -d "${TMPDIR:-/tmp}/octopus-test.XXXXXX"); [ -n "${OCTOPUS_TEST_KEEP:-}" ] && echo "keeping $tmp" || trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"; ln -s "$here/fake-docker.sh" "$tmp/bin/docker"; ln -s "$here/fake-curl.sh" "$tmp/bin/curl"
pass=0; failed=0
check() { if eval "$2"; then pass=$((pass + 1)); echo "  ok   $1"; else failed=$((failed + 1)); echo "  FAIL $1"; fi; }
key="sk-test-$RANDOM$RANDOM-$(date +%s)"; ghcr="ghcr-test-$RANDOM$RANDOM"; hostToken="ht-$RANDOM$RANDOM$RANDOM"; nonce="n$RANDOM$RANDOM"

# sh NAME SCRIPT [env...]: one run in a fresh fake state dir $tmp/NAME; output in $tmp/NAME.out, exit code in $tmp/NAME.code
sh_() {
    local n=$1 s=$2; shift 2
    mkdir -p "$tmp/$n/tmpdir"
    ( env PATH="$tmp/bin:$PATH" TMPDIR="$tmp/$n/tmpdir" FAKE_DIR="$tmp/$n" FWORKER_NAME=cae-42-ab FWORKER_ITEM=42 FWORKER_PHASE=implement FWORKER_MODELPROFILE=default \
        FWORKER_NONCE="$nonce" FWORKER_AGENT=claude FWORKER_MODE=detached FWORKER_GATEWAYURL=https://gw.example.test \
        FWORKER_IMAGE=ghcr.io/clearmeasure-aisf-sample-apps/fworker-host:latest FWORKER_ANTHROPICAPIKEY="$key" \
        FWORKER_GHCRUSERNAME=bot FWORKER_GHCRTOKEN="$ghcr" FWORKER_STARTTIMEOUTSECONDS=4 FWORKER_HOSTTOKEN="$hostToken" \
        OCTOPUS_MACHINE_NAME=host-1 "$@" bash "$scripts/$s" > "$tmp/$n.out" 2>&1; echo $? > "$tmp/$n.code" )
}
code() { cat "$tmp/$1.code"; }
nosecret() { ! grep -rqF -e "$key" -e "$ghcr" -e "$hostToken" "$tmp/$1.out" "$tmp/$1/docker.argv" "$tmp/$1/curl.argv" 2>/dev/null; }

echo "start-worker: detached"
sh_ det start-worker.sh
# shellcheck disable=SC2034 # c is used inside the check strings
c="$tmp/det/c/fw-cae-42-ab"
check "exit 0 after the registration line" '[ "$(code det)" = 0 ] && grep -q "registered at the gateway" "$tmp/det.out"'
check "docker run -d --restart=no, limits, labels" 'grep -q -- "run --name fw-cae-42-ab --restart=no --cpus 2 --memory 8g --env-file .* --label fworker.name=cae-42-ab --label fworker.item=42 --label fworker.nonce=$nonce .* -d ghcr.io/" "$tmp/det/docker.argv"'
check "env file: FWORKER_* values, key mode, the agent key; mode 600" 'grep -qx "FWORKER_NONCE=$nonce" "$c/env" && grep -qx FWORKER_GATEWAY_URL=https://gw.example.test "$c/env" && grep -qx FWRUNNER_AUTH=key "$c/env" && grep -qx "ANTHROPIC_API_KEY=$key" "$c/env" && [ "$(cat "$c/envmode")" = 600 ]'
check "env file removed after the run" '[ -z "$(ls -A "$tmp/det/tmpdir")" ]'
check "GHCR login with the token on stdin, pull" 'grep -qxF "$ghcr" "$tmp/det/login.stdin" && [ -f "$tmp/det/pulled" ]'
check "no secret in the output or any argv" 'nosecret det'

sh_ anon start-worker.sh FWORKER_GHCRUSERNAME= FWORKER_GHCRTOKEN=
check "no GHCR credentials: no docker login, anonymous pull, exit 0" '[ "$(code anon)" = 0 ] && [ ! -e "$tmp/anon/login.stdin" ] && ! grep -qE "(^| )login( |$)" "$tmp/anon/docker.argv" && [ -f "$tmp/anon/pulled" ]'

echo "start-worker: refusals"
sh_ badname start-worker.sh FWORKER_NAME='Bad_Name;rm'
check "bad Fworker.Name: exit 1, no docker call" '[ "$(code badname)" = 1 ] && grep -q "Fworker.Name must match" "$tmp/badname.out" && [ ! -e "$tmp/badname/docker.argv" ]'
sh_ longname start-worker.sh FWORKER_NAME=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
check "41-character name refused" '[ "$(code longname)" = 1 ]'
sh_ badagent start-worker.sh FWORKER_AGENT=gemini
check "unknown agent refused" '[ "$(code badagent)" = 1 ] && grep -q "claude, cursor or codex" "$tmp/badagent.out"'
sh_ nokey start-worker.sh FWORKER_AGENT=cursor
check "cursor without Fworker.CursorApiKey refused" '[ "$(code nokey)" = 1 ] && grep -q "Fworker.CursorApiKey is not set" "$tmp/nokey.out"'
sh_ cursor start-worker.sh FWORKER_AGENT=cursor FWORKER_CURSORAPIKEY="$key"
check "cursor: CURSOR_API_KEY only" '[ "$(code cursor)" = 0 ] && grep -qx "CURSOR_API_KEY=$key" "$tmp/cursor/c/fw-cae-42-ab/env" && ! grep -q ANTHROPIC "$tmp/cursor/c/fw-cae-42-ab/env"'
sh_ codex start-worker.sh FWORKER_AGENT=codex FWORKER_OPENAIAPIKEY="$key"
check "codex: OPENAI_API_KEY" '[ "$(code codex)" = 0 ] && grep -qx "OPENAI_API_KEY=$key" "$tmp/codex/c/fw-cae-42-ab/env" && grep -qx FWRUNNER_AGENT=codex "$tmp/codex/c/fw-cae-42-ab/env"'
mkdir -p "$tmp/dup/c/fw-cae-42-ab"; echo running > "$tmp/dup/c/fw-cae-42-ab/state"
sh_ dup start-worker.sh
check "a running container of the same name: exit 1, not replaced" '[ "$(code dup)" = 1 ] && grep -q "already running" "$tmp/dup.out" && [ ! -e "$tmp/dup/removed" ]'

echo "start-worker: no registration"
sh_ noreg start-worker.sh FAKE_RUN_LOG='starts (gateway x) Bearer leaked?'
check "no registration line within the timeout: exit 1, log dumped, container removed" '[ "$(code noreg)" = 1 ] && grep -q "did not register within 4 s" "$tmp/noreg.out" && grep -q "starts (gateway x)" "$tmp/noreg.out" && grep -qx fw-cae-42-ab "$tmp/noreg/removed"'
sh_ died start-worker.sh FAKE_RUN_LOG='FAILED: redeem: HTTP 410' FAKE_RUN_STATE=exited
check "container ended before registering: exit 1 with its log" '[ "$(code died)" = 1 ] && grep -q "ended (exited) before it registered" "$tmp/died.out" && grep -q "HTTP 410" "$tmp/died.out"'

echo "start-worker: held"
sh_ held start-worker.sh FWORKER_MODE=held FAKE_HELD_CODE=3
check "attached run, exit with the container's code, container removed" '[ "$(code held)" = 3 ] && ! grep -q -- " -d " "$tmp/held/docker.argv" && grep -q "exited with 3" "$tmp/held.out" && grep -qx fw-cae-42-ab "$tmp/held/removed"'
sh_ held0 start-worker.sh FWORKER_MODE=held
check "held, container exits 0: exit 0" '[ "$(code held0)" = 0 ] && nosecret held0'

echo "stop-worker"
mkdir -p "$tmp/stop/c/fw-cae-42-ab"; echo running > "$tmp/stop/c/fw-cae-42-ab/state"; echo last-line > "$tmp/stop/c/fw-cae-42-ab/log"
sh_ stop stop-worker.sh
check "removes the container, logs first" '[ "$(code stop)" = 0 ] && grep -q last-line "$tmp/stop.out" && [ ! -d "$tmp/stop/c/fw-cae-42-ab" ]'
sh_ stop2 stop-worker.sh
check "missing container: exit 0 (idempotent)" '[ "$(code stop2)" = 0 ] && grep -q "nothing to stop" "$tmp/stop2.out"'
sh_ stopbad stop-worker.sh FWORKER_NAME='../x'
check "bad name refused" '[ "$(code stopbad)" = 1 ] && [ ! -e "$tmp/stopbad/docker.argv" ]'

echo "reconcile-hosts"
r="$tmp/rec"; mkdir -p "$r/c"
mk() { mkdir -p "$r/c/fw-$1"; echo "$2" > "$r/c/fw-$1/state"; echo "$3" > "$r/c/fw-$1/item"; echo 2026-10-22T09:00:00Z > "$r/c/fw-$1/started"; echo "log of $1" > "$r/c/fw-$1/log"; }
mk keep running 1; mk gone running 2; mk ended exited 3
printf '{ "workers": [ "keep", "ended" ] }\n' > "$r/desired"
sh_ rec reconcile-hosts.sh
check "exit 0" '[ "$(code rec)" = 0 ]'
check "desired asked for this machine" '[ "$(cat "$r/desired.host")" = host-1 ]'
check "not desired and exited removed, logs in the task log; desired running kept" '[ ! -d "$r/c/fw-gone" ] && [ ! -d "$r/c/fw-ended" ] && [ -d "$r/c/fw-keep" ] && grep -q "log of gone" "$tmp/rec.out" && grep -q "log of ended" "$tmp/rec.out"'
check "report: host and the remaining container" 'grep -qF "{\"host\":\"host-1\",\"containers\":[{\"name\":\"keep\",\"item\":1,\"state\":\"running\",\"startedAt\":\"2026-10-22T09:00:00Z\"}]}" "$r/report.json"'
check "bearer header on both calls, never in argv or output" '[ "$(grep -cxF "Authorization: Bearer $hostToken" "$r/curl.headers")" = 2 ] && nosecret rec'
r="$tmp/recdown"; mkdir -p "$r/c"; mk a running 1
sh_ recdown reconcile-hosts.sh FAKE_CURL_FAIL=1
check "gateway unreachable: exit 1, nothing removed" '[ "$(code recdown)" = 1 ] && [ -d "$r/c/fw-a" ]'
r="$tmp/recbad"; mkdir -p "$r/c"; mk a running 1; echo '{"error":"x"}' > "$r/desired"
sh_ recbad reconcile-hosts.sh
check "answer without workers: exit 1, nothing removed" '[ "$(code recbad)" = 1 ] && [ -d "$r/c/fw-a" ]'
r="$tmp/recinj"; mkdir -p "$r/c"; mk a running 1; echo '{"workers":["a","$(x)"]}' > "$r/desired"
sh_ recinj reconcile-hosts.sh
check "a malformed desired name: exit 1, nothing removed" '[ "$(code recinj)" = 1 ] && [ -d "$r/c/fw-a" ]'
r="$tmp/recempty"; mkdir -p "$r/c"; mk a running 1; echo '{"workers":[]}' > "$r/desired"
sh_ recempty reconcile-hosts.sh
check "empty desired list: every fw-* removed, empty report" '[ "$(code recempty)" = 0 ] && [ ! -d "$r/c/fw-a" ] && grep -qF "\"containers\":[]" "$r/report.json"'
sh_ recnotoken reconcile-hosts.sh FWORKER_HOSTTOKEN=
check "no host token: exit 1" '[ "$(code recnotoken)" = 1 ] && grep -q "HostToken is not set" "$tmp/recnotoken.out"'

echo
echo "octopus runbook tests: $pass passed, $failed failed"
[ "$failed" -eq 0 ]
