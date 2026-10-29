#!/usr/bin/env bash
# Shell test of the Octopus runbook scripts (deploy/octopus/scripts/*.sh) with a fake docker and a fake curl on the PATH. No network, no
# Docker, no Octopus: the scripts read their variables from env (Fworker.Name -> FWORKER_NAME). Secrets are composed at run time.
#   bash tests/octopus/runbooks.test.sh      exit 0 when every check passes
set -u
here=$(cd "$(dirname "$0")" && pwd); root=$(cd "$here/../.." && pwd); scripts=$root/deploy/octopus/scripts
tmp=$(mktemp -d "${TMPDIR:-/tmp}/octopus-test.XXXXXX"); [ -n "${OCTOPUS_TEST_KEEP:-}" ] && echo "keeping $tmp" || trap 'rm -rf "$tmp"' EXIT
mkdir -p "$tmp/bin"; ln -s "$here/fake-docker.sh" "$tmp/bin/docker"; ln -s "$here/fake-curl.sh" "$tmp/bin/curl"; ln -s "$here/fake-df.sh" "$tmp/bin/df"
# the host readings of W7.4 (/proc and the cgroup), as a fixture: 16000 MB in total, 8000 available, 4 cpus, load 0.52; fw-keep peaked at 1536 MB
mkdir -p "$tmp/proc" "$tmp/cg/system.slice/docker-id-fw-keep.scope"
printf 'MemTotal:       16384000 kB\nMemFree:         1000000 kB\nMemAvailable:    8192000 kB\nBuffers:           10000 kB\n' > "$tmp/proc/meminfo"
printf 'processor\t: 0\nmodel name\t: x\n\nprocessor\t: 1\n\nprocessor\t: 2\n\nprocessor\t: 3\n' > "$tmp/proc/cpuinfo"
printf '0.52 0.40 0.30 1/200 999\n' > "$tmp/proc/loadavg"
echo 1610612736 > "$tmp/cg/system.slice/docker-id-fw-keep.scope/memory.peak"
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
        OCTOPUS_MACHINE_NAME=host-1 FWORKER_PROC_DIR="$tmp/proc" FWORKER_CGROUP_DIR="$tmp/cg" "$@" bash "$scripts/$s" > "$tmp/$n.out" 2>&1; echo $? > "$tmp/$n.code" )
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

sh_ spacedphase start-worker.sh FWORKER_PHASE='Technical Design' FWORKER_MODELPROFILE='factory test'
check "a phase and profile with single spaces pass into the env file" '[ "$(code spacedphase)" = 0 ] && grep -qx "FWORKER_PHASE=Technical Design" "$tmp/spacedphase/c/fw-cae-42-ab/env"'

echo "start-worker: refusals"
sh_ badphase start-worker.sh FWORKER_PHASE='Design;rm -rf'
check "a phase with other characters refused, no docker call" '[ "$(code badphase)" = 1 ] && [ ! -e "$tmp/badphase/docker.argv" ]'
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
mkdir -p "$tmp/prune/c/fw-old-1" "$tmp/prune/c/fw-live-2"; echo exited > "$tmp/prune/c/fw-old-1/state"; echo "old log" > "$tmp/prune/c/fw-old-1/log"; echo running > "$tmp/prune/c/fw-live-2/state"
sh_ prune start-worker.sh
check "ended fw-* containers are removed (logs first) before the start; a running one is kept" '[ "$(code prune)" = 0 ] && grep -qx fw-old-1 "$tmp/prune/removed" && ! grep -qx fw-live-2 "$tmp/prune/removed" && grep -q "old log" "$tmp/prune.out"'
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

echo "start-worker: host admission (W7.4)"
mkdir -p "$tmp/proc-busy"; cp "$tmp/proc/meminfo" "$tmp/proc/cpuinfo" "$tmp/proc-busy/"; printf '3.50 3.00 2.00 1/200 999\n' > "$tmp/proc-busy/loadavg"
run1() { mkdir -p "$tmp/$1/c/fw-live-1"; echo running > "$tmp/$1/c/fw-live-1/state"; echo 1 > "$tmp/$1/c/fw-live-1/item"; echo 2026-10-22T09:00:00Z > "$tmp/$1/c/fw-live-1/started"; }
run2() { run1 "$1"; mkdir -p "$tmp/$1/c/fw-live-2"; echo running > "$tmp/$1/c/fw-live-2/state"; echo 2 > "$tmp/$1/c/fw-live-2/item"; echo 2026-10-22T09:00:00Z > "$tmp/$1/c/fw-live-2/started"; }
started() { grep -q -- "run --name fw-cae-42-ab" "$tmp/$1/docker.argv"; }
run1 adm1; sh_ adm1 start-worker.sh FWORKER_PERHOSTCAPACITY=2
check "one running of 2: a guaranteed slot, started, no headroom test" '[ "$(code adm1)" = 0 ] && grep -q "(guaranteed)" "$tmp/adm1.out" && started adm1'
run2 adm2; sh_ adm2 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_HOSTFULLWAITSECONDS=2
check "two running of 2, no burst: host-full, exit 75, nothing started, nothing removed" '[ "$(code adm2)" = 75 ] && grep -q "^host-full: 2 running" "$tmp/adm2.out" && ! started adm2 && [ ! -e "$tmp/adm2/removed" ]'
sh_ adm3 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=1 FWORKER_HOSTFULLWAITSECONDS=2 && :
check "a burst below the capacity never lowers it (limit is max)" '[ "$(code adm3)" = 0 ] && started adm3'
run2 adm4; sh_ adm4 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3
check "a burst slot without a known footprint: host-full" '[ "$(code adm4)" = 75 ] && grep -q "no footprint is known" "$tmp/adm4.out" && ! started adm4'
run2 adm5; sh_ adm5 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000
check "a burst slot with headroom (memory, load, disk): started" '[ "$(code adm5)" = 0 ] && grep -q "(burst)" "$tmp/adm5.out" && started adm5'
run2 adm6; sh_ adm6 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=5952
check "memory: available minus reserve equal to the footprint admits (inclusive)" '[ "$(code adm6)" = 0 ] && started adm6'
run2 adm7; sh_ adm7 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=5953
check "memory: one MB over refuses" '[ "$(code adm7)" = 75 ] && grep -q "burst slot: 8000 MB available" "$tmp/adm7.out" && ! started adm7'
run2 adm8; sh_ adm8 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000 FWORKER_PROC_DIR="$tmp/proc-busy"
check "cpu: load 3.50 + 2 cpus over the host's 4 refuses" '[ "$(code adm8)" = 75 ] && grep -q "is over the host" "$tmp/adm8.out" && ! started adm8'
run2 adm9; sh_ adm9 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000 FAKE_DISK_MB=4095
check "disk: under minDiskMb refuses" '[ "$(code adm9)" = 75 ] && grep -q "4095 MB of disk free" "$tmp/adm9.out" && ! started adm9'
run2 adm10; sh_ adm10 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000 FAKE_DISK_MB=4096
check "disk: equal to minDiskMb admits (inclusive)" '[ "$(code adm10)" = 0 ] && started adm10'
run2 adm11; sh_ adm11 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000 FWORKER_PROC_DIR="$tmp/nonexistent"
check "unreadable host readings fail closed for a burst slot" '[ "$(code adm11)" = 75 ] && grep -q "cannot be read" "$tmp/adm11.out"'
run2 adm12; sh_ adm12 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_PERHOSTBURST=3 FWORKER_FOOTPRINTMB=3000 FWORKER_MEMORY=4g FWORKER_CPUS=1.5 FWORKER_RESERVEMB=1000 FWORKER_MINDISKMB=100
check "reserve, minDisk and fractional cpus are read from the variables" '[ "$(code adm12)" = 0 ] && started adm12'
run2 adm13; ( sleep 1; echo exited > "$tmp/adm13/c/fw-live-2/state" ) &
sh_ adm13 start-worker.sh FWORKER_PERHOSTCAPACITY=2 FWORKER_HOSTFULLWAITSECONDS=10; wait
check "a container that exits within the wait frees the slot" '[ "$(code adm13)" = 0 ] && grep -q "waiting up to 10 s" "$tmp/adm13.out" && started adm13'
run2 adm14; sh_ adm14 start-worker.sh FWORKER_PERHOSTCAPACITY=1 FWORKER_MODE=held
check "held mode (the overflow pool) is not host-limited" '[ "$(code adm14)" = 0 ] && ! grep -q host-full "$tmp/adm14.out"'
sh_ adm15 start-worker.sh FWORKER_PERHOSTCAPACITY=two
check "a non-numeric limit is refused" '[ "$(code adm15)" = 1 ] && [ ! -e "$tmp/adm15/docker.argv" ]'
run2 adm16; sh_ adm16 start-worker.sh
check "no Fworker.PerHostCapacity (every runbook before W7.4): no host test at all" '[ "$(code adm16)" = 0 ] && started adm16 && ! grep -q "Slot " "$tmp/adm16.out"'
check "no secret in a host-full answer" 'nosecret adm2 && nosecret adm4'

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
check "report: host, the remaining container (v1's fields unchanged, memPeakMb added) and the host metrics" 'grep -qF "{\"host\":\"host-1\",\"containers\":[{\"name\":\"keep\",\"item\":1,\"state\":\"running\",\"startedAt\":\"2026-10-22T09:00:00Z\",\"memPeakMb\":1536}],\"memTotalMb\":16000,\"memAvailableMb\":8000,\"cpus\":4,\"load1\":0.52,\"diskFreeMb\":50000}" "$r/report.json"'
check "bearer header on all three calls, never in argv or output" '[ "$(grep -cxF "Authorization: Bearer $hostToken" "$r/curl.headers")" = 3 ] && nosecret rec'
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

r="$tmp/recold"; mkdir -p "$r/c"; mk keep running 1; printf '{"workers":["keep"]}' > "$r/desired"
sh_ recold reconcile-hosts.sh FWORKER_PROC_DIR="$tmp/nonexistent" FWORKER_CGROUP_DIR="$tmp/nonexistent" FAKE_DF_FAIL=1
check "a host that reads nothing reports the v1 body (no metric guessed, no memPeakMb)" '[ "$(code recold)" = 0 ] && grep -qxF "{\"host\":\"host-1\",\"containers\":[{\"name\":\"keep\",\"item\":1,\"state\":\"running\",\"startedAt\":\"2026-10-22T09:00:00Z\"}]}" "$r/report.json"'
r="$tmp/recpart"; mkdir -p "$r/c"; mk keep running 1; printf '{"workers":["keep"]}' > "$r/desired"; mkdir -p "$tmp/proc-part"; cp "$tmp/proc/meminfo" "$tmp/proc-part/"; printf 'garbage\n' > "$tmp/proc-part/loadavg"
sh_ recpart reconcile-hosts.sh FWORKER_PROC_DIR="$tmp/proc-part" FAKE_DISK_MB=1234
check "unreadable metrics are left out, readable ones stay (memory, disk)" 'grep -qF "\"memTotalMb\":16000,\"memAvailableMb\":8000,\"diskFreeMb\":1234}" "$r/report.json" && ! grep -q "load1\|cpus" "$r/report.json"'
r="$tmp/recnolabel"; mkdir -p "$r/c"; mk keep running ""; printf '{"workers":["keep"]}' > "$r/desired"
sh_ recnolabel reconcile-hosts.sh
check "a container with an empty item label is reported with item 0 and the fields stay in place" 'grep -qF "\"name\":\"keep\",\"item\":0,\"state\":\"running\"" "$r/report.json"'

echo
echo "octopus runbook tests: $pass passed, $failed failed"
[ "$failed" -eq 0 ]
