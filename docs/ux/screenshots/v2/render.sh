#!/usr/bin/env bash
# Regenerates the v2 screenshots: renders the populated pages from in-memory models, then screenshots them with Playwright.
# Usage: docs/ux/screenshots/v2/render.sh [prefix]    (default prefix iter1, giving dashboard-iter1-light-1440.png; second argument: name prefix of the shots to take, or "all")
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dump="$(mktemp -d)"
CRM_RENDER_DUMP="$dump" dotnet test "$here/../../../../dotnet/tests/Crm.Tests" --filter "FullyQualifiedName~VisualSnapshotTests"
NODE_PATH="$(npm root -g)" node "$here/render.mjs" "$dump" "$here" "${1:-iter1}" "${2:-dashboard}"
