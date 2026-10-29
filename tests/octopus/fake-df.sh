#!/usr/bin/env bash
# A stand-in for `df -Pm <dir>` in tests/octopus/runbooks.test.sh: the free space is $FAKE_DISK_MB (50000); FAKE_DF_FAIL=1 fails as an unreadable path.
[ "${FAKE_DF_FAIL:-}" = 1 ] && exit 1
printf 'Filesystem 1048576-blocks Used Available Capacity Mounted on\n'
printf '/dev/fake 100000 %s %s 50%% /\n' "$((100000 - ${FAKE_DISK_MB:-50000}))" "${FAKE_DISK_MB:-50000}"
