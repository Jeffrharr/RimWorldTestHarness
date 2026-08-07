using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Profiling;

// The run-level profiling session: one analyzer, started once, harvested once per scenario.
//
// See Shared/RunProfiling.cs for WHY this shape replaced the opt-in Profile step (short version: a
// scenario that activates the analyzer leaves reload-only residue, so a suite of profiling scenarios
// cost one mid-suite reload each — starting the analyzer before the first scenario makes every
// scenario equally instrumented and the residue meaningless). Everything decidable without a game
// lives there and is unit-tested offline; this class is the sequencing and the live state.
//
// THE ORDERING THAT MATTERS. Start() runs after the first map load and before the first scenario's
// first step, not at [StaticConstructorOnStartup]: activating the Harmony-patches entry synchronously
// rewrites the body of every patched method in the load, which needs the load to be finished. The
// analyzer's own per-frame cycle also hangs off Root_Play.Update, so there is nothing to count before
// a Play scene exists.
internal static class RunProfiler
{
    // What the runner asked for. False means this class does nothing at all — no analyzer, no marker,
    // no skip lines — which is what --no-profiler and a hand-launched game both get.
    public static bool Requested { get; private set; }

    // A run-level reason profiling will not happen, known before the analyzer was touched: the runner
    // could not find it on disk, or RimWorld did not load it. Recorded on every scenario's report.
    public static string? RunSkipReason { get; private set; }

    // True once the analyzer is instrumenting the load. This is what ScenarioReport.Profiled records,
    // and therefore what ProbePinning gates against.
    public static bool Active => Requested && RunSkipReason == null && DubsAnalyzer.IsProfiling;

    // Set the moment the driver first reaches a live, interactive game. Its FALSE state is the whole
    // reason requirement-three exists: a run that verifies XML patch behaviour and never reaches a map
    // has no frames to attribute anything to, and must say so rather than report a table of zeroes.
    public static bool GameReached { get; private set; }

    private static bool _started;
    private static string? _startError;

    // Per-scenario window state. _windowOpen is false for a scenario that ran no steps — harvesting
    // then would report the PREVIOUS scenario's frames under this scenario's name.
    private static bool _windowOpen;
    private static int _sampledFrames;
    private static int _pausedFrames;

    // Read once from the environment by HarnessMod, so the runner's intent is stated in one place and
    // nothing downstream re-derives it from whether the analyzer happens to be loaded.
    public static void Configure(bool requested, string? runnerSkipReason)
    {
        Requested = requested;
        RunSkipReason = RunProfiling.BeforeStartSkipReason(requested, runnerSkipReason, DubsAnalyzer.IsLoaded);
        _started = false;
        _startError = null;
        _windowOpen = false;
        GameReached = false;
    }

    public static void NoteGameReached() => GameReached = true;

    // Starts the analyzer, once. Returns the frames the caller should idle before the first scenario's
    // first step: the analyzer transplants timing calls into every profiled method, so the first
    // invocation of each pays JIT for the rewritten IL, and measuring from frame zero would charge that
    // one-off cost to whichever patch happened to run first.
    //
    // A start failure is remembered rather than thrown: profiling is not why the run exists, so the
    // scenarios still run and each one's report carries the reason its table is missing.
    public static int Start()
    {
        if (!Requested || RunSkipReason != null || _started)
            return 0;

        _started = true;
        _startError = DubsAnalyzer.Start();
        if (_startError != null)
        {
            Log.Warning($"RWTH: run-level profiling could not start: {_startError}");
            return 0;
        }

        Log.Message("RWTH: run-level profiling active — every scenario gets a per-patch cost table. " +
                    "Timings in this run, probes included, are measured through an instrumented build.");
        return ProfileExpander.DefaultWarmupFrames;
    }

    // Opens this scenario's window: zeroes the analyzer's counters and the paused-frame tally. Called
    // immediately before a scenario's FIRST step rather than at the scenario boundary, so the settle
    // and warmup frames a boundary burns are not counted as the scenario's own.
    public static void BeginScenario()
    {
        _windowOpen = false;
        _sampledFrames = 0;
        _pausedFrames = 0;

        if (!Active)
            return;

        string? error = DubsAnalyzer.BeginWindow(0);
        if (error != null)
        {
            Log.Warning($"RWTH: could not open a profiling window for this scenario: {error}");
            return;
        }

        // BeginWindow itself sets the reset flag; clearing it here means the flag afterwards records
        // only resets an EXPLICIT Profile step caused, which is the case the note is about.
        DubsAnalyzer.MarkCountersFresh();
        _windowOpen = true;
    }

    // One frame of the scenario, as the driver sees it. `paused` is sampled rather than assumed because
    // run-level profiling deliberately does not force a game speed — see RunProfiling.PausedNote.
    public static void SampleFrame(bool paused)
    {
        if (!_windowOpen)
            return;

        _sampledFrames++;
        if (paused)
            _pausedFrames++;
    }

    // What a scenario's profiling produced: at most one of these is ever non-null.
    public sealed class ScenarioProfile
    {
        public ProfileTable? Table;
        public string? SkipReason;
    }

    // Closes and harvests this scenario's window. Never stops the analyzer — the next scenario needs
    // the same instrumentation or the two tables are not comparable.
    public static ScenarioProfile EndScenario()
    {
        if (!Requested)
            return new ScenarioProfile();

        if (RunSkipReason != null)
            return new ScenarioProfile { SkipReason = RunSkipReason };

        bool windowOpened = _windowOpen;
        DubsAnalyzer.Harvest harvest = windowOpened ? DubsAnalyzer.ReadWindow() : new DubsAnalyzer.Harvest();
        _windowOpen = false;

        // The frame count that decides everything is the ANALYZER's, not the driver's: it is the
        // divisor under every mean in the table. The driver's own _sampledFrames is recorded alongside
        // it as RequestedFrames so a reader can see the two agree (or not).
        string? skip = RunProfiling.AfterWindowSkipReason(
            GameReached, windowOpened, _startError ?? harvest.Error,
            harvest.MeasuredFrames, harvest.Samples.Count);

        if (skip != null)
            return new ScenarioProfile { SkipReason = skip };

        ProfileTable table = ProfileMath.Build(
            RunProfiling.TableName, ProfileExpander.DefaultEntry, RunProfiling.NoPrefix,
            requestedFrames: _sampledFrames, harvest.MeasuredFrames, harvest.FrameMs, harvest.Samples,
            RunProfiling.MaxRows, _sampledFrames, _pausedFrames);

        // Only ever true when an explicit Profile/ProfileMeasure step ran inside this scenario: there is
        // one set of counters in the analyzer, and it re-zeroed them.
        if (DubsAnalyzer.CountersResetSinceMark)
            table.Notes.Add(RunProfiling.CounterResetNote);

        return new ScenarioProfile { Table = table };
    }

    // Torn down with the run, not between scenarios. The transplanted method bodies stay for the life
    // of the process either way (see DubsAnalyzer.Stop); this just stops the per-frame cycle.
    public static void Stop()
    {
        DubsAnalyzer.StopProfiling();
        _windowOpen = false;
    }
}
