#!/usr/bin/env bash
# A stand-in for `claude -p --output-format stream-json` in tests/runner/fwrunner.test.sh. FAKE_MODE chooses the behaviour:
#   end        stream-json lines, the stream-alive file, then the end marker after 2 s; exits on TERM
#   end-stuck  as end, but ignores TERM (fwrunner.sh must KILL it)
#   exit       a few lines, then exits 3 without the marker
#   done       a few lines, then exits 0 without the marker (a manager patrol's normal end)
#   stale      writes nothing and never touches stream-alive
#   mute       touches stream-alive and holds an item, but writes no transcript bytes
home=${FWRUNNER_HOME:?}
cat > "$home/seen-prompt"
printf '%s\n' "$@" > "$home/seen-args"
{ [ -n "${FACTORY_TOKEN:-}" ] && echo token-set; [ -n "${ANTHROPIC_API_KEY:-}" ] && echo api-key-set; pwd; echo "${FACTORY_SESSION_END_FILE:-}"; echo "playwright=${PLAYWRIGHT_BROWSERS_PATH:-}"; } > "$home/seen-env"
line() { printf '{"type":"%s","subtype":"%s","text":"%s"}\n' "$1" "$2" "$3"; }
line system init "session starts"
k=$(printf 'Q%.0s' $(seq 32))
line assistant text "more keys: n1.${k} API-${k} sk-proj-${k} key_${k}"
line assistant text "the worker token is $FACTORY_TOKEN and a pushed token ghs_${FAKE_GHS_SUFFIX:-ZZZZZZZZZZZZZZZZZZZZ} and a key sk-ant-api03-${FAKE_GHS_SUFFIX:-ZZZZ}"
case "${FAKE_MODE:-end}" in
    end|end-stuck)
        [ "$FAKE_MODE" = end-stuck ] && trap '' TERM
        touch "$home/stream-alive"; echo 42 > "$home/session-owning"
        sleep 2; line user tool_result "end-session"
        echo 'end-session: item 42 ended' > "$FACTORY_SESSION_END_FILE.reason"; date -u +%FT%TZ > "$FACTORY_SESSION_END_FILE"
        while :; do sleep 1; done ;;
    exit) sleep 1; exit 3 ;;
    done) sleep 1; exit 0 ;;
    stale) while :; do sleep 1; done ;;
    mute) echo 42 > "$home/session-owning"; while :; do touch "$home/stream-alive"; sleep 1; done ;;
esac
