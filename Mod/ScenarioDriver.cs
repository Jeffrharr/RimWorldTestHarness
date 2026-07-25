using System;
using System.Collections.Generic;
using System.IO;
using RimWorldTestHarness.Shared;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// The tick-driven state machine that replays scenarios against a live game (the BATCH verification
// mode). [StaticConstructorOnStartup] (HarnessMod) fires before any scene/map exists, so nothing here
// can run synchronously from there — Patch_DriveScenario's Root_Play.Update() postfix calls Tick()
// every frame instead, and this class advances one step at a time as each step's wait condition (map
// loaded, screenshot flush, FastForward's tick target) is satisfied.
//
// It drives a SUITE of scenarios in one game load, because a RimWorld boot costs minutes and a step
// costs milliseconds, so N scenarios used to cost N boots. A single-scenario run is the degenerate
// one-entry suite and behaves exactly as it did before suites existed, down to the report shape
// (SuiteReportSerializer) — that path is the fallback and must stay trivial.
//
// Between scenarios it applies the isolation the pure SuitePlanner decided on: nothing, a soft reset
// of the values we can restore (WorldStateReset), or a mid-session save reload (FixtureReloader). All
// of the deciding is in Shared/ and offline-tested; this class only owns the sequencing, the
// DevMode/latitude flags via HarnessRuntime, and folding each StepOutcome into a ScenarioReport.
//
// The actual per-action game logic lives in the shared StepExecutor (so batch and the interactive
// LiveCommandDriver can never drift).
public static class ScenarioDriver
{
    // Read by Tick's own gating. Patch_ForceDevMode/Patch_ForcedLatitude read HarnessRuntime, not
    // this — those flags are shared with the live driver.
    public static bool Active { get; private set; }

    // Burned after the game reports itself initialized, before the first step runs. See ReadyToRun.
    private const int PostLoadSettleFrames = 5;

    // Larger than PostLoadSettleFrames because a mid-session reload runs Root_Play.Start() again, which
    // ends with ScreenFader fading in from black over 0.5s. Five frames in, a Screenshot would capture
    // a partly-black frame — a plausible-looking wrong image on an otherwise green run. 60 frames is
    // >= 0.5s at 60fps, and a lower framerate only makes the wait longer in wall-clock terms, never
    // shorter. The initial-load value is left alone: it is proven by every run to date, and the first
    // load has no fade to outlast (the entry scene already faded in).
    private const int PostReloadSettleFrames = 60;

    // Wall clock, not frames: a load's duration is bounded in seconds, and the frame rate while a long
    // event runs is not something to base a timeout on. Generous, because the point is to eventually
    // fail with a specific reason instead of hanging until run_test.sh's own timeout, which would say
    // nothing about why.
    private const double ReloadTimeoutSeconds = 300;

    private enum State { WaitingForMap, Running, Done }

    private static readonly List<ScenarioSpec> _specs = new();
    private static SuitePlan _plan = new();
    private static SuiteReport _suite = new();
    private static bool _suiteMode;
    private static string? _reloadSaveName;
    private static string? _reportPath;
    private static State _state;
    private static int _scenarioIndex;
    private static int _stepIndex;
    private static int _flushFramesRemaining;
    private static int _settleFramesAfterLoad;
    private static int _fastForwardTargetTicksGame;
    private static bool _waitingForFastForward;
    private static WorldBaseline? _baseline;

    // Non-null only while a mid-session reload is in flight. Holds the Game instance that existed
    // BEFORE the reload was requested, which is what proves the reload actually happened — see
    // ReloadFinished.
    private static object? _gameBeforeReload;
    private static DateTime _reloadDeadlineUtc;

    // Kept as the exact string Runner/run_test.sh greps Player.log for, so its "report exists but the
    // completion marker wasn't seen" warning keeps working for suites as well as single runs.
    private const string RunCompleteMarker = "RWTH: scenario complete";

    private static ScenarioSpec CurrentSpec => _specs[_scenarioIndex];
    private static ScenarioReport CurrentReport => _suite.Scenarios[_scenarioIndex];

    // The single-scenario entry point, unchanged in behaviour: one scenario, no isolation boundaries,
    // and a bare ScenarioReport on disk.
    public static void Begin(ScenarioSpec spec, string reportPath)
    {
        BeginRun(new[] { spec }, SuitePlanner.Single(spec), reportPath, suiteMode: false, reloadSaveName: null);
    }

    // The suite entry point. The plan is passed in already built (by HarnessMod, from the pure
    // SuitePlanner) rather than built here, so the isolation decision stays a pure function of the
    // specs plus the launch options and nothing about it depends on driver state.
    public static void BeginSuite(IReadOnlyList<ScenarioSpec> specs, SuitePlan plan, string reportPath,
                                  string? reloadSaveName, IReadOnlyList<string> launchErrors)
    {
        BeginRun(specs, plan, reportPath, suiteMode: true, reloadSaveName);
        _suite.Errors.AddRange(launchErrors);
    }

    private static void BeginRun(IReadOnlyList<ScenarioSpec> specs, SuitePlan plan, string reportPath,
                                 bool suiteMode, string? reloadSaveName)
    {
        Active = true;
        // Batch mode forces DevMode so vanilla's autostart-save mechanism loads the fixture. Set it
        // synchronously here, before any scene loads, so it's true in time for Root_Entry.Start()'s
        // autostart check (see DESIGN.md).
        HarnessRuntime.ForceDevMode = true;

        _specs.Clear();
        _specs.AddRange(specs);
        _plan = plan;
        _reportPath = reportPath;
        _suiteMode = suiteMode;
        _reloadSaveName = reloadSaveName;

        // Every requested scenario gets a report up front, so an abort partway through still produces a
        // report that lists all of them (see MarkUnreachedScenarios) instead of a shorter one that
        // reads as if the suite were smaller than it was.
        _suite = new SuiteReport();
        for (int i = 0; i < specs.Count; i++)
            _suite.Scenarios.Add(SuiteReportBuilder.ForScenario(specs[i]));

        _suite.Errors.AddRange(plan.Errors);
        _suite.IsolationNotes.AddRange(plan.Notes);
        _suite.IsolationNotes.AddRange(DescribePlan(plan));

        _state = State.WaitingForMap;
        _settleFramesAfterLoad = PostLoadSettleFrames;
        _scenarioIndex = 0;
        _stepIndex = 0;
    }

    // Called every frame from Patch_DriveScenario.
    public static void Tick()
    {
        if (!Active || _state == State.Done)
            return;

        // The WaitingForMap branch runs BEFORE the long-event guard, unlike everything below it,
        // because that is the state a mid-session reload sits in: the reload happens inside a long
        // event, so a timeout that only counted down outside one would never fire.
        if (_state == State.WaitingForMap)
        {
            AdvanceWaitingForMap();
            return;
        }

        // Mirrors Root_Play.Update()'s own LongEventHandler guard, since a Harmony postfix runs even
        // when the original method's guard clause returned early.
        if (LongEventHandler.ShouldWaitForEvent)
            return;

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

    private static void AdvanceWaitingForMap()
    {
        if (ReloadTimedOut())
        {
            AbortSuite(
                $"mid-suite reload of save '{_reloadSaveName}' did not complete within " +
                $"{ReloadTimeoutSeconds:0}s — no scenario after this point ran");
            return;
        }

        if (LongEventHandler.ShouldWaitForEvent)
            return;
        if (!ReadyToRun())
            return;

        _state = State.Running;
        // Give the game a few frames after FinalizeInit before the first step observes it. Cheap
        // insurance on top of the ProgramState gate, reusing the existing flush counter.
        _flushFramesRemaining = _settleFramesAfterLoad;
        // Re-captured on every load, not just the first: a reload returns the world to the save's own
        // state, which is precisely what a later soft reset should restore to.
        _baseline = WorldStateReset.Capture();
        _gameBeforeReload = null;
    }

    private static bool ReloadTimedOut() =>
        _gameBeforeReload != null && DateTime.UtcNow > _reloadDeadlineUtc;

    // A non-null CurrentMap is NOT sufficient, which cost a full debugging session to learn.
    // Game.InitNewGame sets ProgramState.MapInitializing, generates the map (so Find.CurrentMap goes
    // non-null), and only then calls FinalizeInit, which sets ProgramState.Playing. On the -quicktest
    // path Root_Play.Update therefore pumps this driver while the new game is still being set up:
    // steps ran against a half-built map and produced a storm of ArgumentOutOfRangeException out of
    // Verse.TickList.Tick, with no map ever rendering — and because those are RimWorld's own logged
    // errors rather than step failures, the run still reported Pass.
    //
    // The autostart/fixture path never hit it because Game.LoadGame runs inside a LongEvent, so the
    // ShouldWaitForEvent guard above covered the same window by accident. ProgramState.Playing is the
    // real signal, and both InitNewGame and LoadGame funnel through FinalizeInit to set it.
    private static bool ReadyToRun() =>
        ReloadFinished() && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null;

    // The extra gate a mid-session reload needs, and the reason the ProgramState check alone is not
    // enough to re-arm readiness per load: GameDataSaveLoader.LoadGame only QUEUES a long event. For
    // the frame or two before that event becomes current, ShouldWaitForEvent is still false (there is
    // no currentEvent yet and the queued one isn't the standard-window kind), ProgramState is still
    // Playing and CurrentMap is still the old map — indistinguishable from "the reload already
    // finished". Requiring a different Game instance is the postcondition that says the reload
    // genuinely happened rather than that we never left; vanilla replaces Current.Game three times
    // over the course of one load, so the identity always changes.
    private static bool ReloadFinished() =>
        _gameBeforeReload == null || !ReferenceEquals(_gameBeforeReload, Current.Game);

    private static void RunNextStep()
    {
        // Defence in depth for an EMPTY suite (a suite list of nothing but comments, say). The runner's
        // own preflight refuses that, and SuitePlanner records it as an error — but without this guard
        // CurrentSpec would index an empty list and throw inside the Update postfix every frame, which
        // hangs the run instead of reporting the error it already has.
        if (_scenarioIndex >= _specs.Count)
        {
            Finish();
            return;
        }

        if (_stepIndex >= CurrentSpec.Steps.Count)
        {
            FinishScenario();
            return;
        }

        ScenarioStep step = CurrentSpec.Steps[_stepIndex];
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
    //
    // In a suite the name is qualified with the scenario that asked for it, because two independently
    // authored scenarios will happily both ask for "shot.png" and the second would silently overwrite
    // the first — a green run over the wrong images. Single runs are left unqualified so their output
    // filenames are exactly what they have always been. SuiteScreenshots owns the rule and
    // Runner/run_test.sh mirrors it in bash to find a suite's timelapse frames.
    private static string ResolveScreenshotPath(string fileName)
    {
        string dir = Path.GetDirectoryName(_reportPath!) ?? ".";
        string name = _suiteMode ? SuiteScreenshots.Qualify(CurrentSpec.Name, fileName) : fileName;
        return Path.Combine(dir, name);
    }

    private static void ApplyOutcome(ScenarioStep step, StepOutcome outcome)
    {
        if (outcome.Error != null)
        {
            // Recorded and moved past, never fatal to the suite: one bad step must not truncate the
            // rest of its own scenario, let alone the scenarios after it. The error still fails this
            // scenario (ReportComparer.AllPass counts errors) and therefore the suite.
            CurrentReport.Errors.Add(outcome.Error);
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
            CurrentReport.ProbeChecks.Add(ReportComparer.CheckProbe(probeName, actual, expected, tolerance));
        }

        if (outcome.ScreenshotPath != null)
            CurrentReport.ScreenshotPaths.Add(outcome.ScreenshotPath);

        if (outcome.WaitFrames > 0)
            _flushFramesRemaining = outcome.WaitFrames;

        if (outcome.WaitFastForward)
        {
            _waitingForFastForward = true;
            _fastForwardTargetTicksGame = outcome.FastForwardTargetTicksGame;
        }
    }

    private static void FinishScenario()
    {
        ScenarioReport report = CurrentReport;
        // Errors count toward Pass, not just probe checks: a scenario whose steps failed verified less
        // than it claims to, and with no Probe step at all an errors-ignoring gate reports Pass over
        // an empty check list. See ReportComparer's two-arg overload.
        report.Pass = ReportComparer.AllPass(report.ProbeChecks, report.Errors);
        Log.Message($"RWTH: scenario finished: {report.ScenarioName} pass={report.Pass}");

        _scenarioIndex++;
        if (_scenarioIndex >= _specs.Count)
        {
            Finish();
            return;
        }

        StartNextScenario();
    }

    // Applies the planned isolation, then arms the next scenario. Only ever called for entries after
    // the first — the first scenario's isolation is the fresh load itself.
    // Both _flushFramesRemaining and _waitingForFastForward are provably already clear here: Tick
    // satisfies each of them BEFORE it calls RunNextStep, and FinishScenario is only reached from
    // RunNextStep. They're deliberately not reset defensively, because zeroing a wait that turned out
    // to be genuine would skip it silently.
    private static void StartNextScenario()
    {
        _stepIndex = 0;

        IsolationAction action = _plan.Entries[_scenarioIndex].Before;
        Log.Message($"RWTH: starting scenario {_scenarioIndex + 1}/{_specs.Count} " +
                    $"'{CurrentSpec.Name}' (isolation: {action})");

        if (action == IsolationAction.ReloadSave)
        {
            RequestReload();
            return;
        }

        if (action == IsolationAction.SoftReset)
            ApplySoftReset();
    }

    private static void ApplySoftReset()
    {
        string? error = WorldStateReset.Restore(_baseline!);
        if (error != null)
        {
            // Recorded against the suite rather than the scenario: the reset is the harness's own
            // bookkeeping between scenarios, not a step the scenario asked for. It also means the
            // scenario runs anyway, on a world of unknown cleanliness — which is why the suite can no
            // longer pass.
            _suite.Errors.Add(error);
        }

        // Same reason the initial load burns settle frames: the clock jump and camera move don't
        // necessarily propagate through the glow grid and shadow direction in the frame they happen, and
        // the next scenario's first step could otherwise observe stale lighting.
        _flushFramesRemaining = PostLoadSettleFrames;
    }

    private static void RequestReload()
    {
        if (_reloadSaveName == null)
        {
            // Unreachable via SuitePlanner, which only ever plans a reload when reloadAvailable is
            // true. Guarded anyway rather than dereferenced: a planner/driver disagreement should
            // surface as a named suite error, not a NullReferenceException inside a Update postfix.
            AbortSuite("a reload was planned but no reload save name was supplied to the driver");
            return;
        }

        // Ordered so the driver is already refusing to step before the reload is requested: the frame
        // right after LoadGame is one where the guards below are the only thing standing between us
        // and stepping against a half-disposed Game.
        _gameBeforeReload = Current.Game;
        _reloadDeadlineUtc = DateTime.UtcNow.AddSeconds(ReloadTimeoutSeconds);
        _settleFramesAfterLoad = PostReloadSettleFrames;
        _state = State.WaitingForMap;

        string? error = FixtureReloader.Reload(_reloadSaveName);
        if (error != null)
            AbortSuite($"mid-suite reload failed: {error}");
    }

    // Ends the run early. Distinct from Finish only in that it records why; both go through Finish so
    // a report is always written and the game always quits.
    private static void AbortSuite(string error)
    {
        _suite.Errors.Add(error);
        Finish();
    }

    private static void Finish()
    {
        _state = State.Done;
        MarkUnreachedScenarios();
        _suite.Pass = ReportComparer.AllPass(_suite);
        File.WriteAllText(_reportPath!, SuiteReportSerializer.Serialize(_suite, _suiteMode));
        // Redundant signal alongside the report file itself — Runner/run_test.sh polls for the
        // report's existence primarily, but this gives a human skimming Player.log the same
        // answer without needing to go find the report.
        Log.Message(RunCompleteMarker);
        Active = false;
        // Release the batch-only flags so nothing leaks past the run.
        HarnessRuntime.ForceDevMode = false;
        HarnessRuntime.ForcedLatitude = null;
        // Screenshot steps hide the UI and deliberately don't restore it (the capture finishes over
        // later frames), so the run's last screenshot leaves it hidden. Clearing it here keeps that
        // contained to the run.
        HarnessDebugActions.SetScreenshotMode(false);
        Application.Quit();
    }

    // A suite that aborted must still account for every scenario it was asked to run. Leaving the
    // unreached ones as untouched Pass=false entries would be almost as bad as omitting them: they'd
    // look like scenarios that ran and failed, which is a different thing to investigate.
    private static void MarkUnreachedScenarios()
    {
        for (int i = _scenarioIndex; i < _suite.Scenarios.Count; i++)
            _suite.Scenarios[i].Errors.Add(SuiteReportBuilder.NotRunReason);
    }

    // The plan in words, into the report and (via the caller) Player.log. A suite that reloaded four
    // times and shared a world twice should say so where the results are read, not only in whatever
    // reasoning produced it.
    private static List<string> DescribePlan(SuitePlan plan)
    {
        List<string> lines = new List<string>();
        for (int i = 0; i < plan.Entries.Count; i++)
            lines.Add(DescribeEntry(plan.Entries[i]));

        return lines;
    }

    private static string DescribeEntry(SuiteEntryPlan entry) =>
        $"{entry.ScenarioIndex + 1}. {entry.ScenarioName}: before={entry.Before}, " +
        $"leaves behind {ScenarioResidueAnalyzer.Describe(entry.Residue)}";
}
