#!/usr/bin/env bash
# A stand-in for `docker` in tests/octopus/runbooks.test.sh. Containers are directories $FAKE_DIR/c/<name> with files state, item, started,
# log, env (the env file's content at run). Every call's argv is appended to $FAKE_DIR/docker.argv.
# FAKE_RUN_LOG: the log a started container has (default: a line with "nonce redeemed"); FAKE_RUN_STATE: its state (running);
# FAKE_HELD_CODE: the exit code of an attached run (0). `info` answers the data root; a container's id is id-<name>.
set -u
d=${FAKE_DIR:?}; mkdir -p "$d/c"
printf '%s\n' "$*" >> "$d/docker.argv"
cmd=$1; shift
case "$cmd" in
    login) cat > "$d/login.stdin"; echo ok ;;
    info) echo "${FAKE_DOCKER_ROOT:-/var/lib/docker}" ;;
    pull) echo "pulled $*" > "$d/pulled" ;;
    inspect)
        fmt=''; [ "$1" = --format ] && { fmt=$2; shift 2; }
        c=$1; [ -d "$d/c/$c" ] || exit 1
        case "$fmt" in
            '{{.State.Status}}') cat "$d/c/$c/state" ;;
            '') echo '[{}]' ;;
            *) it=$(cat "$d/c/$c/item")   # the scripts' template says: or (label) "0", so the field is never empty
               printf '/%s\t%s\t%s\t%s\t%s\n' "$c" "${it:-0}" "$(cat "$d/c/$c/state")" "$(cat "$d/c/$c/started")" "id-$c" ;;
        esac ;;
    rm) [ "$1" = -f ] && shift; for c in "$@"; do rm -rf "${d:?}/c/$c"; echo "$c" >> "$d/removed"; done ;;
    logs) [ "$1" = --tail ] && shift 2; c=$1; [ -d "$d/c/$c" ] || exit 1; cat "$d/c/$c/log" ;;
    ps) for p in "$d"/c/fw-*; do [ -d "$p" ] || continue; n=$(basename "$p")
            case "$*" in *'{{.State}}'*) printf '%s\t%s\n' "$n" "$(cat "$p/state")" ;; *) echo "$n" ;; esac; done ;;
    run)
        name=''; envf=''; detach=0; item=''
        while [ $# -gt 1 ]; do
            case "$1" in --name) name=$2; shift ;; --env-file) envf=$2; shift ;; -d) detach=1 ;;
                --label) case "$2" in fworker.item=*) item=${2#fworker.item=} ;; esac; shift ;;
                --cpus|--memory) shift ;; esac
            shift
        done
        mkdir -p "$d/c/$name"; cp "$envf" "$d/c/$name/env"; stat -c %a "$envf" > "$d/c/$name/envmode"
        echo "$item" > "$d/c/$name/item"; echo 2026-10-22T10:00:00Z > "$d/c/$name/started"
        printf '%s\n' "${FAKE_RUN_LOG-2026-10-22T10:00:01Z fwrunner x: nonce redeemed}" > "$d/c/$name/log"
        if [ "$detach" = 1 ]; then echo "${FAKE_RUN_STATE:-running}" > "$d/c/$name/state"; echo "id-$name"
        else echo exited > "$d/c/$name/state"; cat "$d/c/$name/log"; exit "${FAKE_HELD_CODE:-0}"; fi ;;
    *) echo "fake docker: unknown command $cmd" >&2; exit 2 ;;
esac
