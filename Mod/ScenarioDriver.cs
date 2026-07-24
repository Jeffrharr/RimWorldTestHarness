using System.IO;
using System.Text.Json;
using RimWorldTestHarness.Shared;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// The tick-driven state machine that replays a ScenarioSpec's steps against a live game (the BATCH
// verification mode). [StaticConstructorOnStartup] (HarnessMod) fires before any scene/map exists,
// so nothing here can run synchronously from there — Patch_DriveScenario's Root_Play.Update()
// postfix calls Tick() every frame instead, and this class advances one step at a time as each
// step's wait condition (map loaded, screenshot flush, FastForward's tick target) is satisfied.
//
// The actual per-action game logic lives in the shared StepExecutor (so batch and the interactive
// LiveCommandDriver can never drift); this class only owns the batch concerns: sequencing the
// scenario's steps, the DevMode/latitude flags via HarnessRuntime, and folding each StepOutcome
// into a ScenarioReport.
public static class ScenarioDriver
{
    // Read by Tick's own gating. Patch_ForceDevMode/Patch_ForcedLatitude read HarnessRuntime, not
    // this — those flags are shared with the live driver.
    public static bool Active { get; private set; }

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
        // Batch mode forces DevMode so vanilla's autostart-save mechanism loads the fixture. Set it
        // synchronously here, before any scene loads, so it's true in time for Root_Entry.Start()'s
        // autostart check (see DESIGN.md).
        HarnessRuntime.ForceDevMode = true;
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
        StepContext ctx = new StepContext(Find.CurrentMap, ResolveScreenshotPath);
        StepOutcome outcome = StepExecutor.Execute(step.Type, step.Args, ctx);
        ApplyOutcome(step, outcome);
    }

    // Screenshots for a batch run land next to the report file, keeping every artifact of one run in
    // the same Runner/reports/ folder.
    private static string ResolveScreenshotPath(string fileName)
    {
        string dir = Path.GetDirectoryName(_reportPath!) ?? ".";
        return Path.Combine(dir, fileName);
    }

    private static void ApplyOutcome(ScenarioStep step, StepOutcome outcome)
    {
        if (outcome.Error != null)
        {
            _report!.Errors.Add(outcome.Error);
            return;
        }

        if (outcome.ForcedLatitude is float latitude)
            HarnessRuntime.ForcedLatitude = latitude;

        if (outcome.ProbeValue is float actual)
        {
            // The raw reading comes from the shared executor; the pass/fail comparison against this
            // step's expected/tolerance is batch-specific and stays here.
            string probeName = step.Args[StepArgs.ProbeName];
            float expected = float.Parse(step.Args[StepArgs.ProbeExpectedValue]);
            float tolerance = float.Parse(step.Args[StepArgs.ProbeTolerance]);
            _report!.ProbeChecks.Add(ReportComparer.CheckProbe(probeName, actual, expected, tolerance));
        }

        if (outcome.ScreenshotPath != null)
            _report!.ScreenshotPaths.Add(outcome.ScreenshotPath);

        if (outcome.WaitFrames > 0)
            _flushFramesRemaining = outcome.WaitFrames;

        if (outcome.WaitFastForward)
        {
            _waitingForFastForward = true;
            _fastForwardTargetTicksGame = outcome.FastForwardTargetTicksGame;
        }
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
        // Release the batch-only flags so nothing leaks past the run.
        HarnessRuntime.ForceDevMode = false;
        HarnessRuntime.ForcedLatitude = null;
        Application.Quit();
    }
}
