#!/usr/bin/env bash
# The presentation run: open-loop traffic (operations issued at a fixed rate whether or not earlier ones
# have completed, as requests arrive at a service) with a tight command timeout, starting with
# notifications off. Press F for the "before", N to switch to Auto, F again for the "after".
#
#   ./present.sh                  3000 ops/s, 300ms timeout, starts Disabled
#   ./present.sh --mode Auto      start with notifications on
#   RATE=5000 TIMEOUT=250 ./present.sh
#
# Anything else is passed through to the demo (later arguments win), so the target is chosen as usual:
# HITLESS_ENDPOINTS, --endpoints <file>, --endpoint host:port, or hitless.local.json (see README).
set -euo pipefail
cd "$(dirname "$0")"
exec dotnet run -c Release -- --rate "${RATE:-3000}" --timeout "${TIMEOUT:-300}" --mode Disabled "$@"
