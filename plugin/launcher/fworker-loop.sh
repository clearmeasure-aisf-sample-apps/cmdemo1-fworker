#!/usr/bin/env bash
# fworker-loop.sh: runs factory worker sessions one after another, one work item per session (docs/launcher.md).
#
#   fworker-loop.sh --dir BASE --prompt-file FILE [--backoff S] [--max-backoff S] [--stop-file FILE] [--claude CMD] [-- claude arguments...]
#
# Each round starts a new `claude` session with the connect prompt (the content of FILE) as its first message. When the worker's item has ended the
# worker runs `factory.ps1 end-session`, which writes BASE/work/session-ended (this script exports its path as FACTORY_SESSION_END_FILE); the script
# then ends that session and starts the next one after a short pause. A session that exits by itself (a crash, or a person quit it) is started again
# too, with a pause that doubles up to --max-backoff.
# Stop: create the stop file (default BASE/work/STOP; the session that runs finishes its item first), or press Ctrl-C while no session runs (the
# pause between sessions). Needs only bash and `claude` on the PATH (or --claude).
set -u

dir=''; prompt=''; backoff=10; maxBackoff=300; stopFile=''; claudeCmd='claude'; extra=()
while [ $# -gt 0 ]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --prompt-file) prompt="$2"; shift 2 ;;
        --backoff) backoff="$2"; shift 2 ;;
        --max-backoff) maxBackoff="$2"; shift 2 ;;
        --stop-file) stopFile="$2"; shift 2 ;;
        --claude) claudeCmd="$2"; shift 2 ;;
        --) shift; extra=("$@"); break ;;
        -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "fworker-loop: unknown argument $1 (see --help)" >&2; exit 2 ;;
    esac
done
[ -n "$dir" ] && [ -n "$prompt" ] || { echo "fworker-loop: --dir and --prompt-file are required (see --help)" >&2; exit 2; }
[ -f "$prompt" ] || { echo "fworker-loop: no prompt file at $prompt" >&2; exit 2; }
case "$backoff$maxBackoff" in *[!0-9]*) echo "fworker-loop: --backoff and --max-backoff are whole seconds" >&2; exit 2 ;; esac
command -v "$claudeCmd" >/dev/null 2>&1 || { echo "fworker-loop: $claudeCmd is not on the PATH" >&2; exit 2; }
mkdir -p "$dir/work"
marker="$dir/work/session-ended"
[ -n "$stopFile" ] || stopFile="$dir/work/STOP"
export FACTORY_SESSION_END_FILE="$marker"

stopping=0; child=0
on_signal() { stopping=1; if [ "$child" -gt 0 ] && kill -0 "$child" 2>/dev/null; then kill -TERM "$child" 2>/dev/null; fi; }
trap on_signal INT TERM
say() { printf '%s fworker-loop: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }
# a pause that Ctrl-C ends at once (the trap runs while `wait` waits, not only after a foreground sleep)
pause() { sleep "$1" & local s=$!; wait "$s" 2>/dev/null; kill "$s" 2>/dev/null; }

delay=$backoff; round=0
while [ "$stopping" -eq 0 ]; do
    if [ -e "$stopFile" ]; then say "stop file $stopFile is present: not starting another session"; break; fi
    rm -f "$marker"
    round=$((round + 1)); say "session $round starts"
    # the session reads the terminal: a background job of a script would otherwise get /dev/null as its input
    if [ -t 0 ]; then "$claudeCmd" ${extra[@]+"${extra[@]}"} "$(cat "$prompt")" </dev/tty & else "$claudeCmd" ${extra[@]+"${extra[@]}"} "$(cat "$prompt")" & fi
    child=$!
    ended=0
    while kill -0 "$child" 2>/dev/null; do
        if [ -e "$marker" ]; then
            ended=1; say "the worker ended its session (end marker written); stopping it"
            pause 5     # the worker's last lines are written
            kill -TERM "$child" 2>/dev/null
            for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 "$child" 2>/dev/null || break; sleep 1; done
            kill -KILL "$child" 2>/dev/null
            break
        fi
        sleep 2
    done
    wait "$child" 2>/dev/null; code=$?; child=0
    [ -e "$marker" ] && ended=1
    stty sane 2>/dev/null </dev/tty || true     # a session stopped by a signal can leave the terminal in raw mode
    [ "$stopping" -eq 1 ] && break
    if [ "$ended" -eq 1 ]; then delay=$backoff; say "session $round finished its item"
    else say "session $round exited by itself (exit $code)"; fi
    say "next session in $delay s (Ctrl-C or the stop file $stopFile to stop)"
    pause "$delay"
    [ "$ended" -eq 1 ] || { delay=$((delay * 2)); [ "$delay" -le "$maxBackoff" ] || delay=$maxBackoff; }
done
say "stopped"
