#!/bin/bash
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"

# Runner/asset_claims.py is python, not C#, because it is the runner's own bookkeeping — but it is
# the part of the runner with real branching to get wrong (what was here before, is it still what we
# installed, what does undoing it mean), so it gets the same treatment as the Shared/ logic: offline
# tests over a tmpdir, no game and no lock. Run first because it is the fastest gate by two orders of
# magnitude.
# Deliberately not given "$@" — those are dotnet test's flags, and unittest would reject them.
cd "$ROOT"
python3 -m unittest discover -s Tests/runner

cd "$ROOT/Tests/RimWorldTestHarness.Tests"
/home/deck/.dotnet/dotnet test "$@"

# Separate project (not merged into RimWorldTestHarness.Tests above) because its tests are
# [Category("RequiresGameDll")] — they need the real installed Assembly-CSharp.dll and
# UnityEngine.ScreenCaptureModule.dll to mean anything, and Assert.Ignore themselves out if those
# aren't found at the hardcoded Steam path (override via RIMWORLD_ASSEMBLY /
# RIMWORLD_SCREENCAPTURE_ASSEMBLY env vars).
cd "$ROOT/Tests/RimWorldTestHarness.ApiTests"
/home/deck/.dotnet/dotnet test "$@"
