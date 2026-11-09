#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"

for file in *.apparmor *.service *.socket *.cfg.in *.kafel.in *.c *.sh; do
    if LC_ALL=C grep -q $'\r' -- "$file"; then
        printf 'FAIL non-LF line endings in %s\n' "$file" >&2
        exit 1
    fi
done
printf '%s\n' 'PASS LF in Linux sandbox files'

if ! command -v apparmor_parser >/dev/null; then
    printf '%s\n' 'NOT RUN AppArmor syntax (apparmor_parser unavailable)' >&2
    exit 77
fi

apparmor_parser -Q -K ./coderank-judge-sandbox.apparmor
printf '%s\n' 'PASS AppArmor syntax (profile not loaded)'
