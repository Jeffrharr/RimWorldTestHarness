#!/bin/bash
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"

cd "$ROOT/Tests/RimWorldTestHarness.Tests"
/home/deck/.dotnet/dotnet test "$@"

# Separate project (not merged into RimWorldTestHarness.Tests above) because its tests are
# [Category("RequiresGameDll")] — they need the real installed Assembly-CSharp.dll and
# UnityEngine.ScreenCaptureModule.dll to mean anything, and Assert.Ignore themselves out if those
# aren't found at the hardcoded Steam path (override via RIMWORLD_ASSEMBLY /
# RIMWORLD_SCREENCAPTURE_ASSEMBLY env vars).
cd "$ROOT/Tests/RimWorldTestHarness.ApiTests"
/home/deck/.dotnet/dotnet test "$@"
