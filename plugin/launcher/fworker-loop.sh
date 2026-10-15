#!/usr/bin/env bash
# fworker-loop.sh: runs factory worker sessions one after another, one work item per session (docs/launcher.md).
#
#   fworker-loop.sh --dir BASE --prompt-file FILE [--backoff S] [--max-backoff S] [--stop-file FILE] [--claude CMD]
#                   [--stream-stale S] [--silent S] [--unsent S] [--screen-cmd CMD] [-- claude arguments...]
#
# Each round starts a new `claude` session with the connect prompt (the content of FILE) as its first message. When the worker's item has ended the
# worker runs `factory.ps1 end-session`, which writes BASE/work/session-ended (this script exports its path as FACTORY_SESSION_END_FILE); the script
# then ends that session and starts the next one after a short pause. A session that exits by itself (a crash, or a person quit it) is started again
# too, with a pause that doubles up to --max-backoff.
# Stop: create the stop file (default BASE/work/STOP; the session that runs finishes its item first), or press Ctrl-C while no session runs (the
# pause between sessions). Needs only bash and `claude` on the PATH (or --claude).
# Watchdog: the session is also ended and restarted when its event stream showed no line for --stream-stale seconds (default 600), when its screen
# did not change for --silent seconds while it holds an item (default 1800), or when its prompt holds typed, unsent input for --unsent seconds
# (default 600). The screen is read with --screen-cmd, default `tmux capture-pane -p` when the session runs in tmux; without one the screen checks
# are off. 0 switches a check off. Every restart and its reason is written to BASE/work/launcher.log.
set -u

dir=''; prompt=''; backoff=10; maxBackoff=300; stopFile=''; claudeCmd='claude'; extra=(); streamStale=600; silent=1800; unsent=600; screenCmd=''
while [ $# -gt 0 ]; do
    case "$1" in
        --dir) dir="$2"; shift 2 ;;
        --prompt-file) prompt="$2"; shift 2 ;;
        --backoff) backoff="$2"; shift 2 ;;
        --max-backoff) maxBackoff="$2"; shift 2 ;;
        --stop-file) stopFile="$2"; shift 2 ;;
        --claude) claudeCmd="$2"; shift 2 ;;
        --stream-stale) streamStale="$2"; shift 2 ;;
        --silent) silent="$2"; shift 2 ;;
        --unsent) unsent="$2"; shift 2 ;;
        --screen-cmd) screenCmd="$2"; shift 2 ;;
        --) shift; extra=("$@"); break ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        *) echo "fworker-loop: unknown argument $1 (see --help)" >&2; exit 2 ;;
    esac
done
[ -n "$dir" ] && [ -n "$prompt" ] || { echo "fworker-loop: --dir and --prompt-file are required (see --help)" >&2; exit 2; }
[ -f "$prompt" ] || { echo "fworker-loop: no prompt file at $prompt" >&2; exit 2; }
case "$backoff$maxBackoff$streamStale$silent$unsent" in *[!0-9]*) echo "fworker-loop: --backoff, --max-backoff, --stream-stale, --silent and --unsent are whole seconds" >&2; exit 2 ;; esac
command -v "$claudeCmd" >/dev/null 2>&1 || { echo "fworker-loop: $claudeCmd is not on the PATH" >&2; exit 2; }
mkdir -p "$dir/work"
marker="$dir/work/session-ended"
[ -n "$stopFile" ] || stopFile="$dir/work/STOP"
export FACTORY_SESSION_END_FILE="$marker"
alive="$dir/work/stream-alive"; owning="$dir/work/session-owning"; log="$dir/work/launcher.log"
if [ -z "$screenCmd" ] && [ -n "${TMUX_PANE:-}" ] && command -v tmux >/dev/null 2>&1; then screenCmd="tmux capture-pane -p -t $TMUX_PANE"; fi

stopping=0; child=0
on_signal() { stopping=1; if [ "$child" -gt 0 ] && kill -0 "$child" 2>/dev/null; then kill -TERM "$child" 2>/dev/null; fi; }
trap on_signal INT TERM
say() { printf '%s fworker-loop: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }
# a restart and its reason: on the terminal and in BASE/work/launcher.log
logRestart() { say "restart session $round: $1"; printf '%s session %s restart: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$round" "$1" >> "$log" 2>/dev/null || true; }
mtime() { stat -c %Y "$1" 2>/dev/null || stat -f %m "$1" 2>/dev/null || echo 0; }
# the text typed into the live prompt, or nothing: only the bottom-most prompt line among the last 8 non-blank lines of the screen counts (the transcript
# above shows earlier messages with the same marker), without the box border, the "Try ..." placeholder or a numbered menu row. Plain bash string
# operations: some greps mishandle a multibyte character in an alternation, and a C locale has no multibyte bracket expressions.
typedInput() {
    local l last=''
    while IFS= read -r l; do
        l=${l//│/ }; l=${l//❯/>}; l=${l//|/ }
        if [[ $l =~ ^\ *[\>](\ |$) ]]; then last=$l; fi
    done <<EOF_SCREEN
$(printf '%s\n' "$1" | grep -v '^[[:space:]]*$' | tail -n 8)
EOF_SCREEN
    last=${last#"${last%%[! ]*}"}; last=${last#>}
    last=${last#"${last%%[! ]*}"}; last=${last%"${last##*[! ]}"}
    case "$last" in ''|'Try "'*) return 0 ;; esac
    [[ $last =~ ^[0-9]+\. ]] && return 0
    printf '%s' "$last"
}
# the watchdog's verdict for the running session in wd, or empty (no subshell: it keeps the screen's last change in globals): stream silence, screen silence while it holds an item, typed input left unsent
watchdog() {
    wd=''; local now; now=$(date +%s)
    if [ "$streamStale" -gt 0 ]; then
        local ref=$started; [ -e "$alive" ] && ref=$(mtime "$alive"); [ "$ref" -ge "$started" ] || ref=$started
        if [ $((now - ref)) -ge "$streamStale" ]; then wd="stream-disconnected: no line on the event stream for $((now - ref)) s"; return; fi
    fi
    [ -n "$screenCmd" ] || return 0
    local scr sum; scr=$(sh -c "$screenCmd" </dev/null 2>/dev/null) || return 0
    sum=$(printf '%s' "$scr" | cksum)
    if [ "$sum" != "$lastSum" ]; then lastSum=$sum; screenAt=$now; return 0; fi
    local quiet=$((now - screenAt))
    if [ "$unsent" -gt 0 ] && [ "$quiet" -ge "$unsent" ] && [ -n "$(typedInput "$scr")" ]; then
        wd="unsent-input: typed input sat unsent in the prompt for $quiet s"; return; fi
    if [ "$silent" -gt 0 ] && [ -e "$owning" ] && [ "$quiet" -ge "$silent" ]; then wd="no-output-while-owning: the screen did not change for $quiet s while the session holds item $(cat "$owning" 2>/dev/null)"; return; fi
}
# a pause that Ctrl-C ends at once (the trap runs while `wait` waits, not only after a foreground sleep)
pause() { sleep "$1" & local s=$!; wait "$s" 2>/dev/null; kill "$s" 2>/dev/null; }

delay=$backoff; round=0
while [ "$stopping" -eq 0 ]; do
    if [ -e "$stopFile" ]; then say "stop file $stopFile is present: not starting another session"; break; fi
    rm -f "$marker" "$marker.reason" "$alive" "$owning"
    started=$(date +%s); screenAt=$started; lastSum=''; checkAt=0; restarted=''
    round=$((round + 1)); say "session $round starts"
    # the session reads the terminal: a background job of a script would otherwise get /dev/null as its input
    if [ -t 0 ]; then "$claudeCmd" ${extra[@]+"${extra[@]}"} "$(cat "$prompt")" </dev/tty & else "$claudeCmd" ${extra[@]+"${extra[@]}"} "$(cat "$prompt")" & fi
    child=$!
    ended=0
    while kill -0 "$child" 2>/dev/null; do
        why=''
        if [ -e "$marker" ]; then
            why=$(cat "$marker.reason" 2>/dev/null); [ -n "$why" ] || why='end-session: the worker ended its session'
            logRestart "$why"
            pause 5     # the worker's last lines are written
        elif [ "$(date +%s)" -ge "$checkAt" ]; then
            checkAt=$(( $(date +%s) + 5 )); watchdog; why=${wd:+watchdog $wd}
            [ -z "$why" ] || logRestart "$why"
        fi
        if [ -n "$why" ]; then
            ended=1; restarted=$why
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
    if [ "$ended" -eq 1 ]; then delay=$backoff; case "$restarted" in watchdog*) say "session $round was restarted by the watchdog" ;; *) say "session $round finished its item" ;; esac
    else say "session $round exited by itself (exit $code)"; fi
    say "next session in $delay s (Ctrl-C or the stop file $stopFile to stop)"
    pause "$delay"
    [ "$ended" -eq 1 ] || { delay=$((delay * 2)); [ "$delay" -le "$maxBackoff" ] || delay=$maxBackoff; }
done
say "stopped"
