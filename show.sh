#!/usr/bin/env bash
# Show one of a demo's presentation files, with bat/batcat if installed, else cat.
# Used through the wrappers, from the repo root or anywhere else:
#
#   ./coreusage.sh caching          show caching/CoreUsage.cs - the library calls the demo is about
#   ./setup.sh caching              show caching/Setup.cs     - how the connection is configured
#   ./coreusage.sh                  list the demos that have a CoreUsage.cs
#   ./coreusage.sh caching -r 20:60 further arguments go to bat (here: a line range)
#
# The demo is named as for run.sh (caching, hybridcache/tui, hitless/present, ...). A folder
# that only has the file further down (geofailover -> geofailover/tui) resolves if that is unambiguous.
set -euo pipefail

root="$(cd "$(dirname "$0")" && pwd)"
file="${1:?usage: show.sh <File.cs> [demo] [bat args...]}"; shift
self="${SHOW_AS:-$0 $file}"

candidates() { # <folder>: every copy of the file at or below it, relative to the repo root
    find "$1" \( -name bin -o -name obj -o -name '.*' \) -prune -o -type f -name "$file" -print |
        LC_ALL=C sort | while IFS= read -r p; do echo "${p#"$root"/}"; done
}

if [[ $# -lt 1 ]]; then
    echo "usage: $self <demo> [bat args...]"
    echo
    echo "demos with a $file:"
    candidates "$root" | while IFS= read -r p; do echo "    $(dirname "$p")"; done
    exit 0
fi

demo="${1%/}"; shift
[[ "$demo" == "hitless/present" ]] && demo="hitless" # run.sh's preset is the hitless demo
dir="$demo"
[[ -d "$dir" ]] || dir="$root/$demo"
if ! dir="$(cd "$dir" 2>/dev/null && pwd)"; then
    echo "$demo: no such demo; run $self with no arguments for the list" >&2
    exit 2
fi

target="$dir/$file"
if [[ ! -f "$target" ]]; then
    mapfile -t found < <(candidates "$dir")
    case ${#found[@]} in
        0) echo "$demo has no $file; run $self with no arguments for the demos that do" >&2; exit 1 ;;
        1) target="$root/${found[0]}" ;;
        *) echo "$demo has more than one $file; pick one:" >&2
           for p in "${found[@]}"; do echo "    $self $(dirname "$p")" >&2; done
           exit 2 ;;
    esac
fi

# Debian/Ubuntu install bat as batcat
for viewer in batcat bat; do
    command -v "$viewer" >/dev/null && exec "$viewer" "$@" "$target"
done
exec cat "$target"
