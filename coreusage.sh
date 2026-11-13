#!/usr/bin/env bash
# Show a demo's CoreUsage.cs (bat/batcat if installed, else cat): ./coreusage.sh caching
# With no arguments, lists the demos that have one. See show.sh.
SHOW_AS="$0" exec "$(dirname "$0")/show.sh" CoreUsage.cs "$@"
