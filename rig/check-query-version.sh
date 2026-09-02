#!/usr/bin/env bash
# Fails when solver behaviour changed but the solve cache was not invalidated.
#
# Cached results live in data/cache keyed by LineupApi's QueryVersion constant,
# so a change to how lineups are found, filtered, or ranked keeps serving
# answers computed by the previous code until someone remembers to bump it.
# Nobody remembered seven times running (technical_debt.md A31-H3): the roof-
# target fix, the mid-air origin fix, crates-and-ledges, exact-target re-aim,
# the exposed penalty, easier-throws-first, and standing the thrower on the
# floor all shipped against a stale cache.
#
# Usage: check-query-version.sh <base-ref>
set -euo pipefail

BASE="${1:?usage: check-query-version.sh <base-ref>}"
VERSION_FILE="src/Cli/Services/LineupApi.cs"

# Directories whose behaviour the cached answer depends on.
WATCHED=(src/Solver src/Sim "$VERSION_FILE" src/Cli/Services/TargetSolver.cs)

changed=$(git diff --name-only "$BASE" -- "${WATCHED[@]}")
if [ -z "$changed" ]; then
  echo "No solver-affecting changes; QueryVersion bump not required."
  exit 0
fi

current=$(grep -oP 'const int QueryVersion = \K[0-9]+' "$VERSION_FILE")
base=$(git show "$BASE:$VERSION_FILE" 2>/dev/null | grep -oP 'const int QueryVersion = \K[0-9]+' || echo "")

if [ -z "$base" ]; then
  echo "Could not read QueryVersion from $BASE; skipping."
  exit 0
fi

echo "Solver-affecting files changed:"
echo "$changed" | sed 's/^/  /'
echo "QueryVersion: $base (base) -> $current (head)"

if [ "$current" -gt "$base" ]; then
  echo "OK: cache invalidated."
  exit 0
fi

cat >&2 <<EOF

FAIL: solver-affecting files changed but QueryVersion is still $current.

Every warm entry in data/cache will keep replaying answers computed by the old
code, so this change will not reach anyone whose query is already cached.

Bump it in $VERSION_FILE:

    const int QueryVersion = $((current + 1));

If this change genuinely cannot alter any lineup, origin, ranking, or response
field - a comment, a rename, a test - say so in the PR and re-run.
EOF
exit 1
