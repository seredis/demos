#!/usr/bin/env bash
# Run a demo as a native-AOT binary (Release): start its Docker infrastructure, compile it if
# needed, run it.
#
#   ./run.sh                          list the available demos
#   ./run.sh compacthash              run compacthash
#   ./run.sh compacthash -- --foo     everything after the demo (and an optional --) goes to the demo
#   REBUILD=1 ./run.sh compacthash    force a recompile
#   NO_DOCKER=1 ./run.sh compacthash  leave the infrastructure alone (it is already up, or elsewhere)
#   ./run.sh hitless/present          a preset: the hitless demo with its presentation arguments
#   ./run.sh --all                    prebuild every demo that is missing or stale; run nothing
#   REBUILD=1 ./run.sh --all          recompile every demo
#
# <demo> is a folder holding exactly one .csproj (so: compacthash, hybridcache/console, ...).
# A folder that holds several demos means its tui/: `./run.sh geofailover` runs geofailover/tui.
#
# Infrastructure: if the demo's folder, or a parent of it inside this repo, has a
# docker-compose.yml, `docker compose up -d --wait` runs there first. That is a no-op taking well
# under a second when the containers are already up. Nothing is torn down afterwards; use
# `docker compose down` in that folder. --all does not touch Docker.
#
# Binary: lands in <demo>/bin/Release/aot/ (bin/ is git-ignored), next to a fingerprint of the sources it
# was built from; it is rebuilt when missing or when that fingerprint no longer matches.
# The folder is deliberately as deep as `dotnet run`'s bin/Release/net10.0/: some demos locate
# files relative to the binary (vectorsets keeps its MNIST download in ../../../data).
#
# PublishAot is passed on the command line rather than set in the csproj, so the demos' ordinary
# `dotnet run` is untouched. Trim/AOT analysis warnings are printed by the publish: read them, they
# are the answer to "does this demo really work under AOT, or does it merely link?".
# Needs clang (or gcc) and zlib headers, as for any native-AOT publish on Linux.
set -euo pipefail
shopt -s nullglob

root="$(cd "$(dirname "$0")" && pwd)"

# every folder under the repo holding a .csproj, as a path relative to the repo root
list_demos() {
    find "$root" \( -name bin -o -name obj -o -name '.*' \) -prune -o -type f -name '*.csproj' -print |
        LC_ALL=C sort | while IFS= read -r p; do
            d="$(dirname "$p")"
            echo "${d#"$root"/}"
        done
}

# Staleness is decided by content, not timestamps: a hash over the names and contents of every
# build input. That is right after `git pull`, after switching branch and back (no needless
# rebuild), and when a file is deleted or renamed - all cases where mtimes mislead.
# Not covered: a changed SDK or toolchain; use REBUILD=1 for that.
fingerprint() { # <demo dir>
    {
        find "$1" \( -name bin -o -name obj \) -prune -o \
            -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' \) -print0
        find "$root" -maxdepth 1 -type f \( -name 'Directory.*' -o -name 'global.json' -o -name 'nuget.config' \) -print0
    } | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1
}

# Resolve a demo folder, then publish it if missing or stale. Sets: dir, exe, built, warnings.
ensure_built() { # <demo folder, absolute or relative to cwd or to the repo root>
    dir="$1"
    [[ -d "$dir" ]] || dir="$root/$dir"
    if ! dir="$(cd "$dir" 2>/dev/null && pwd)"; then
        echo "$1: no such demo; run $0 with no arguments for the list" >&2
        return 2
    fi
    local projects=("$dir"/*.csproj)
    # a folder of several demos (geofailover, hybridcache): prefer the interactive one
    if [[ ${#projects[@]} -eq 0 && -d "$dir/tui" ]]; then
        dir="$dir/tui"
        projects=("$dir"/*.csproj)
    fi
    if [[ ${#projects[@]} -ne 1 ]]; then
        echo "$dir: expected exactly one .csproj, found ${#projects[@]}" >&2
        return 2
    fi
    local project="${projects[0]}" out="$dir/bin/Release/aot"
    local name; name="$(basename "$project" .csproj)"
    local stamp="$out/.fingerprint" current reason=""
    exe="$out/$name"
    built=0
    warnings=0

    current="$(fingerprint "$dir")"
    if [[ -n "${REBUILD:-}" ]]; then reason="Rebuild requested"
    elif [[ ! -x "$exe" || ! -f "$stamp" ]]; then reason="First run"
    elif [[ "$(<"$stamp")" != "$current" ]]; then reason="Sources changed"
    fi
    [[ -z "$reason" ]] && return 0

    echo "$reason, precompiling $name as native AOT (Release); this takes a moment..." >&2
    rm -f "$stamp" # a failed publish must not leave a stamp that vouches for an old binary
    local log; log="$(mktemp)"
    if ! dotnet publish "$project" -c Release -p:PublishAot=true -o "$out" --nologo -v:quiet 2>&1 | tee "$log" >&2; then
        rm -f "$log"
        return 1
    fi
    warnings="$(grep -E 'warning [A-Z]+[0-9]+' "$log" | sort -u | wc -l)"
    rm -f "$log"
    echo "$current" > "$stamp"
    built=1
}

# Start the demo's containers: the nearest docker-compose.yml at or above the demo folder.
ensure_infrastructure() { # <demo dir, absolute>
    [[ -n "${NO_DOCKER:-}" ]] && return 0
    local d="$1"
    while [[ ! -f "$d/docker-compose.yml" ]]; do
        [[ "$d" == "$root" || "$d" == "/" ]] && return 0 # no compose file: nothing to start
        d="$(dirname "$d")"
    done
    if ! command -v docker >/dev/null; then
        echo "docker not found; ${d#"$root"/}/docker-compose.yml not started (NO_DOCKER=1 silences this)" >&2
        return 0
    fi
    # anything not yet running means a pull or build, which can take a while: say so
    local want have
    want="$(cd "$d" && docker compose config --services | wc -l)"
    have="$(cd "$d" && docker compose ps -q --status running | wc -l)"
    if [[ "$have" -lt "$want" ]]; then
        echo "Starting infrastructure (docker compose up -d --wait in ${d#"$root"/})..." >&2
        (cd "$d" && docker compose up -d --wait --quiet-pull) >&2
    else
        # already running: still wait for healthy, but only speak up on failure
        local log
        if ! log="$(cd "$d" && docker compose up -d --wait 2>&1)"; then
            echo "$log" >&2
            return 1
        fi
    fi
}

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <demo> [--] [demo args...]"
    echo "       $0 --all        prebuild every demo, run nothing"
    echo
    echo "demos (* = prebuilt, though possibly stale; the folder alone means its tui, e.g. geofailover):"
    list_demos | while IFS= read -r d; do
        mark=' '
        for p in "$root/$d"/*.csproj; do
            [[ -x "$root/$d/bin/Release/aot/$(basename "$p" .csproj)" ]] && mark='*'
        done
        echo "  $mark $d"
        # presets: a second name for a demo, with arguments supplied (see the case statement below)
        [[ "$d" == "hitless" ]] && echo "  $mark hitless/present   (preset: --rate \${RATE:-3000} --timeout \${TIMEOUT:-300} --mode Disabled)"
    done
    exit 0
fi

if [[ "$1" == "--all" ]]; then
    # keep going past a failure: the summary is the useful part
    mapfile -t demos < <(list_demos)
    results=()
    failed=0
    for d in "${demos[@]}"; do
        if ensure_built "$d"; then
            if [[ $built -eq 0 ]]; then results+=("up to date     $d")
            elif [[ $warnings -eq 0 ]]; then results+=("built          $d")
            else results+=("built          $d  ($warnings distinct warning(s), see above)")
            fi
        else
            results+=("FAILED         $d")
            failed=1
        fi
    done
    echo
    printf '  %s\n' "${results[@]}"
    exit $failed
fi

demo="$1"; shift
[[ "${1:-}" == "--" ]] && shift

# Presets. hitless/present is the presentation run: open-loop traffic (operations issued at a fixed
# rate whether or not earlier ones have completed, as requests arrive at a service) with a tight
# command timeout, starting with notifications off. Press F for the "before", N to switch to Auto,
# F again for the "after".
#   ./run.sh hitless/present                  3000 ops/s, 300ms timeout, starts Disabled
#   ./run.sh hitless/present --mode Auto      start with notifications on (later arguments win)
#   RATE=5000 TIMEOUT=250 ./run.sh hitless/present
# The target is chosen as usual: HITLESS_ENDPOINTS, --endpoints <file>, --endpoint host:port, or
# hitless.local.json (see hitless/README.md).
case "$demo" in
    hitless/present)
        demo="hitless"
        set -- --rate "${RATE:-3000}" --timeout "${TIMEOUT:-300}" --mode Disabled "$@"
        ;;
esac

ensure_built "$demo" || exit $?
ensure_infrastructure "$dir" || exit $?

cd "$dir" # the demos expect to run from their own folder (local config files, docker-compose.yml)
exec "$exe" "$@"
