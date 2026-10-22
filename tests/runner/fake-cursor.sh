#!/usr/bin/env bash
# A stand-in for `cursor-agent -p --output-format stream-json --force <prompt>` in tests/runner/fwrunner.test.sh (FWRUNNER_AGENT=cursor,
# FWRUNNER_AUTH=key). Records its args and environment facts, then behaves like fake-claude.sh's FAKE_MODE=end or FAKE_MODE=exit.
home=${FWRUNNER_HOME:?}
printf '%s\n' "$@" > "$home/seen-args"
{ [ -n "${FACTORY_TOKEN:-}" ] && echo token-set; [ -n "${CURSOR_API_KEY:-}" ] && echo cursor-key-set; [ -n "${ANTHROPIC_API_KEY:-}" ] && echo anthropic-key-set; pwd; } > "$home/seen-env"
line() { printf '{"type":"%s","subtype":"%s","text":"%s"}\n' "$1" "$2" "$3"; }
line system init "session starts"
line assistant text "the worker token is $FACTORY_TOKEN"
case "${FAKE_MODE:-end}" in
    end)
        touch "$home/stream-alive"; echo 42 > "$home/session-owning"
        sleep 2; line result success "end-session"
        echo 'end-session: item 42 ended' > "$FACTORY_SESSION_END_FILE.reason"; date -u +%FT%TZ > "$FACTORY_SESSION_END_FILE"
        while :; do sleep 1; done ;;
    exit) sleep 1; exit 3 ;;
esac
