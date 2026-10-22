#!/usr/bin/env bash
# A stand-in for `curl` in tests/octopus/runbooks.test.sh. Records argv ($FAKE_DIR/curl.argv), the headers read from -H @file
# ($FAKE_DIR/curl.headers) and a POST body from --data-binary @- ($FAKE_DIR/report.json). A GET of .../desired answers $FAKE_DIR/desired;
# FAKE_CURL_FAIL=1 fails every call (exit 7, as an unreachable host).
set -u
d=${FAKE_DIR:?}
printf '%s\n' "$*" >> "$d/curl.argv"
[ "${FAKE_CURL_FAIL:-}" = 1 ] && exit 7
url=''; post=0
while [ $# -gt 0 ]; do
    case "$1" in
        -H) case "$2" in @*) cat "${2#@}" >> "$d/curl.headers" ;; *) printf '%s\n' "$2" >> "$d/curl.headers" ;; esac; shift ;;
        -X) [ "$2" = POST ] && post=1; shift ;;
        --data-binary) [ "$2" = @- ] && cat > "$d/report.json"; shift ;;
        --max-time) shift ;;
        http*) url=$1 ;;
    esac
    shift
done
case "$url" in
    */api/v1/runtime/octopus/desired\?host=*) echo "${url##*host=}" > "$d/desired.host"; cat "$d/desired" ;;
    */api/v1/runtime/octopus/report) [ "$post" = 1 ] || exit 22 ;;
    *) exit 22 ;;
esac
