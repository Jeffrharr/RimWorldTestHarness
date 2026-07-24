using System.IO;
using System.Text.Json;
using RimWorld;
using RimWorldTestHarness.Mod.Probes;
using RimWorldTestHarness.Shared;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// The tick-driven state machine that actually replays a ScenarioSpec's steps against a live game.
// [StaticConstructorOnStartup] (HarnessMod) fires before any scene/map exists, so nothing here can
// run synchronously from there — Patch_DriveScenario's Root_Play.Update() postfix calls Tick()
// every frame instead, and this class advances one step at a time as each step's wait condition
// (map loaded, screenshot flush, FastForward's tick target) is satisfied. No HarmonyLib dependency
// here on purpose — the live-game hookup lives entirely in Patch_DriveScenario/Patch_ForceDevMode/
// Patch_ForcedLatitude, mirroring the pure-core/thin-adapter split the parent CLAUDE.md asks for,
// even though this class still touches Verse/UnityEngine types directly (it IS the impure side —
// Shared/ is the pure half).
public static class ScenarioDriver
{
    // Read by Patch_ForceDevMode / Patch_ForcedLatitude, which stay generic Harmony postfixes —
    // this class is the only thing that knows whether a scenario is actually running.
    public static bool Active { get; private set; }
    public static float? ForcedLatitude { get; private set; }

    private enum State { WaitingForMap, Running, Done }

    private static ScenarioSpec? _spec;
    private static ScenarioReport? _report;
    private static string? _reportPath;
    private static State _state;
    private static int _stepIndex;
    private static int _flushFramesRemaining;
    private static int _fastForwardTargetTicksGame;
    private static bool _waitingForFastForward;

    public static void Begin(ScenarioSpec spec, string reportPath)
    {
        Active = true;
        _spec = spec;
        _reportPath = reportPath;
        _report = new ScenarioReport { ScenarioName = spec.Name };
        _state = State.WaitingForMap;
        _stepIndex = 0;
    }

    // Called every frame from Patch_DriveScenario. Mirrors Root_Play.Update()'s own
    // LongEventHandler guard, since a Harmony postfix runs even when the original method's guard
    // clause returned early.
    public static void Tick()
    {
        if (!Active || _state == State.Done)
            return;
        if (LongEventHandler.ShouldWaitForEvent)
            return;

        if (_state == State.WaitingForMap)
        {
            if (Find.CurrentMap == null)
                return;
            _state = State.Running;
        }

        if (_flushFramesRemaining > 0)
        {
            _flushFramesRemaining--;
            return;
        }

        if (_waitingForFastForward)
        {
            if (Find.TickManager.TicksGame < _fastForwardTargetTicksGame)
                return;
            _waitingForFastForward = false;
        }

        RunNextStep();
    }

    private static void RunNextStep()
    {
        if (_spec == null)
            return;

        if (_stepIndex >= _spec.Steps.Count)
        {
            Finish();
            return;
        }

        ScenarioStep step = _spec.Steps[_stepIndex];
        _stepIndex++;
        RunStep(step);
    }

    private static void RunStep(ScenarioStep step)
    {
        switch (step.Type)
        {
            case StepArgs.SetTileType:
                RunSetTile(step);
                break;
            case StepArgs.SetSeasonType:
                RunSetSeason(step);
                break;
            case StepArgs.SetTimeType:
                RunSetTime(step);
                break;
            case StepArgs.FastForwardType:
                RunFastForward(step);
                break;
            case StepArgs.ProbeType:
                RunProbe(step);
                break;
            case StepArgs.ScreenshotType:
                RunScreenshot(step);
                break;
            default:
                _report!.Errors.Add($"Unknown step type '{step.Type}'");
                break;
        }
    }

    // A scenario just overrides latitude in place (Patch_ForcedLatitude) rather than actually
    // regenerating the fixture's landing tile — see Patch_ForcedLatitude.cs and
    // Fixtures/README.md for why.
    private static void RunSetTile(ScenarioStep step)
    {
        ForcedLatitude = float.Parse(step.Args[StepArgs.SetTileLatitude]);
    }

    private static void RunSetSeason(ScenarioStep step)
    {
        int dayOfYear = int.Parse(step.Args[StepArgs.SetSeasonDayOfYear]);
        float longitude = CurrentLongitude();
        float currentHour = GenDate.HourFloat(Find.TickManager.TicksAbs, longitude);
        JumpToLocalTime(dayOfYear, currentHour, longitude);
    }

    private static void RunSetTime(ScenarioStep step)
    {
        float hour = float.Parse(step.Args[StepArgs.SetTimeHour]);
        float longitude = CurrentLongitude();
        int currentDayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longitude);
        JumpToLocalTime(currentDayOfYear, hour, longitude);
    }

    private static void RunFastForward(ScenarioStep step)
    {
        int ticks = int.Parse(step.Args[StepArgs.FastForwardTicks]);
        _fastForwardTargetTicksGame = Find.TickManager.TicksGame + ticks;
        _waitingForFastForward = true;
        // Unlike SetSeason/SetTime (which jump the clock directly), FastForward needs real ticks
        // to actually pass, so make sure the game isn't sitting paused while we wait.
        Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
    }

    private static void RunProbe(ScenarioStep step)
    {
        string probeName = step.Args[StepArgs.ProbeName];
        float expected = float.Parse(step.Args[StepArgs.ProbeExpectedValue]);
        float tolerance = float.Parse(step.Args[StepArgs.ProbeTolerance]);

        if (!ProbeRegistry.TryGet(probeName, out IProbe? probe) || probe == null)
        {
            _report!.Errors.Add($"No probe registered named '{probeName}'");
            return;
        }

        float actual = probe.Read(Find.CurrentMap);
        _report!.ProbeChecks.Add(ReportComparer.CheckProbe(probeName, actual, expected, tolerance));
    }

    private static void RunScreenshot(ScenarioStep step)
    {
        string fileName = step.Args[StepArgs.ScreenshotFileName];
        string dir = Path.GetDirectoryName(_reportPath!) ?? ".";
        string path = Path.Combine(dir, fileName);
        ScreenCapture.CaptureScreenshot(path);
        _report!.ScreenshotPaths.Add(path);
        // CaptureScreenshot writes asynchronously over the next few frames — give it time to
        // flush before the next step (or Finish()) runs.
        _flushFramesRemaining = 5;
    }

    private static void Finish()
    {
        _state = State.Done;
        _report!.Pass = ReportComparer.AllPass(_report.ProbeChecks);
        File.WriteAllText(_reportPath!, JsonSerializer.Serialize(_report));
        // Redundant signal alongside the report file itself — Runner/run_test.sh polls for the
        // report's existence primarily, but this gives a human skimming Player.log the same
        // answer without needing to go find the report.
        Log.Message("RWTH: scenario complete");
        Active = false;
        Application.Quit();
    }

    private static float CurrentLongitude() => Find.WorldGrid.LongLatOf(Find.CurrentMap.Tile).x;

    // Jumps the game clock so GenDate.DayOfYear/HourFloat read back exactly dayOfYear/hour at the
    // given longitude, staying within the current in-game year. This mirrors GenDate's own
    // DayOfYear/HourOfDay derivation (PositiveModRemap over TicksAbs + a longitude offset) in
    // reverse — see the decompiled reference in the harness's implementation plan doc.
    private static void JumpToLocalTime(int dayOfYear, float hour, float longitude)
    {
        long offset = GenDate.LocalTicksOffsetFromLongitude(longitude);
        long currentAbs = Find.TickManager.TicksAbs;
        long currentLocal = currentAbs + offset;
        long yearStart = currentLocal - PositiveMod(currentLocal, GenDate.TicksPerYear);

        int dayTick = Mathf.Clamp(Mathf.RoundToInt(hour * GenDate.TicksPerHour), 0, GenDate.TicksPerDay - 1);
        long targetLocal = yearStart + (long)dayOfYear * GenDate.TicksPerDay + dayTick;
        long targetAbs = targetLocal - offset;

        long gameStartAbsTick = currentAbs - Find.TickManager.TicksGame;
        Find.TickManager.DebugSetTicksGame((int)(targetAbs - gameStartAbsTick));
    }

    private static long PositiveMod(long value, long modulus) => ((value % modulus) + modulus) % modulus;
}
