using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod.Profiling;

// The adapter between the harness and Dubs Performance Analyzer (Steam Workshop 2038874626). Every
// member of the analyzer is reached by reflection, and nothing outside this file names one.
//
// WHY REFLECTION RATHER THAN A REFERENCE. The analyzer cannot be a dependency in either direction. It
// is a Workshop mod, so a compile-time reference would mean the harness only builds on a machine that
// has it downloaded — and, more importantly, an assembly-level reference is resolved by Mono when a
// method touching those types is first JITted, so an install without the analyzer would take a
// TypeLoadException somewhere unrelated instead of a clean "not installed" message. Reflection makes
// absence a value we can test for and report, which is the whole requirement: the step must no-op
// LOUDLY, never silently.
//
// AND WHY THE ANALYZER IS NEVER IN AN ORDINARY RUN'S LOAD ORDER. A profiler changes what a run
// measures — it rewrites the body of every Harmony-patched method in the load to add timing calls.
// Runner/run_test.sh only puts it in ModsConfig when --profiler is passed, and a scenario that uses it
// declares ScenarioResidue.Profiler so nothing after it shares the same game load.
//
// WHAT WE DRIVE, AND WHY IT LOOKS LIKE Window_Analyzer.PreOpen. The analyzer has no API for
// "profile headlessly"; its entry point is a window opening. Start() therefore mirrors PreOpen's
// sequence — load the entry list, Harmony-patch Root_Play.Update/TickManager.DoSingleTick so the
// per-frame measurement cycle runs, activate an entry, BeginProfiling — minus the GUI. Harvesting
// reads Profiler.CollectStatistics directly rather than Analyzer.Logs, because Logs is rebuilt on a
// background Task a couple of times a second and reading it at an arbitrary moment would give a
// window whose length we did not choose.
internal static class DubsAnalyzer
{
    // The analyzer entry that profiles every non-analyzer Harmony patch in the load, by patch method.
    // This is the one that attributes cost to MOD names rather than to vanilla methods, which is what
    // makes a per-mod table possible at all.
    private const string HarmonyPatchesEntryTypeName = "H_HarmonyPatches";

    private const string AnalyzerTypeName = "Analyzer.Profiling.Analyzer";

    // Reported verbatim by every step when the analyzer is absent. Named in Shared so the runner-side
    // wording and the in-game wording cannot drift; see Shared/RunProfiling.cs.
    public const string NotLoadedReason = RunProfiling.NotLoadedReason;

    private static object? _activeEntry;

    // True from the first successful Start() until StopProfiling(). Guards Start against being run
    // twice — run-level profiling starts the analyzer after the first map load, and an explicit
    // ProfileStart step later in the same run would otherwise re-run entry activation and re-pay the
    // synchronous instrumentation of the whole modlist for no change in state.
    public static bool IsProfiling { get; private set; }

    // Set by BeginWindow, cleared by whoever opened the window it belongs to. There is exactly ONE set
    // of counters inside the analyzer, so an explicit Profile step inside a scenario silently
    // reschedules the run-level window that wraps it. The flag is how the run-level table gets to say
    // so (RunProfiling.CounterResetNote) instead of quietly describing a shorter span than its name
    // implies.
    public static bool CountersResetSinceMark { get; private set; }

    public static void MarkCountersFresh() => CountersResetSinceMark = false;

    // Resolved once. A null means "not in this load", which is a normal, expected state — not an
    // error to log about.
    public static bool IsLoaded => AnalyzerType != null;

    private static Type? AnalyzerType => Lazy.Analyzer;

    // Cached type/member lookups. Static readonly rather than resolved per call because the step runs
    // inside Root_Play.Update's postfix and AccessTools.TypeByName walks every loaded assembly.
    private static class Lazy
    {
        public static readonly Type? Analyzer = AccessTools.TypeByName(AnalyzerTypeName);
        public static readonly Type? ProfileController = AccessTools.TypeByName("Analyzer.Profiling.ProfileController");
        public static readonly Type? GUIController = AccessTools.TypeByName("Analyzer.Profiling.GUIController");
        public static readonly Type? Entry = AccessTools.TypeByName("Analyzer.Profiling.Entry");
        public static readonly Type? Tab = AccessTools.TypeByName("Analyzer.Profiling.Tab");
        public static readonly Type? Profiler = AccessTools.TypeByName("Analyzer.Profiling.Profiler");
        public static readonly Type? Window = AccessTools.TypeByName("Analyzer.Window_Analyzer");
        public static readonly Type? Modbase = AccessTools.TypeByName("Analyzer.Modbase");
        public static readonly Type? Settings = AccessTools.TypeByName("Analyzer.Settings");
        public static readonly Type? RootUpdate = AccessTools.TypeByName("Analyzer.Profiling.H_RootUpdate");
        public static readonly Type? SingleTick = AccessTools.TypeByName("Analyzer.Profiling.H_DoSingleTickUpdate");
    }

    // Activates profiling. Returns null on success, or a human-readable reason it could not start.
    //
    // Callers treat a NotLoadedReason return as a scenario SKIP and anything else as a step error: the
    // first is an install this scenario cannot apply to, the second is something that went wrong.
    public static string? Start()
    {
        if (!IsLoaded)
            return NotLoadedReason;

        // Idempotent: a run-level session and a later explicit ProfileStart step both want profiling
        // on, and the second one wanting it is not a reason to instrument the load a second time.
        if (IsProfiling)
            return null;

        try
        {
            LoadEntriesOnce();
            AccessTools.Method(AnalyzerType, "BeginProfiling").Invoke(null, null);
            PatchUpdateCycleOnce();
            string? error = ActivateHarmonyPatchesEntry();
            IsProfiling = error == null;
            return error;
        }
        catch (Exception ex)
        {
            // Unwrapped: a reflected call reports its real failure inside TargetInvocationException,
            // and the outer message ("Exception has been thrown by the target of an invocation") says
            // nothing at all in a report.
            return $"failed to start Dubs Performance Analyzer: {Unwrap(ex)}";
        }
    }

    // Zeroes every profiler and the analyzer's own frame counter, opening the measured window. Split
    // from Start because the analyzer transplants timing calls into method bodies, so the first
    // invocation of each profiled method pays JIT for the rewritten IL — measuring from frame zero
    // charges that one-off cost to whichever patch happened to run first.
    //
    // `requestedFrames` is carried through to the harvest purely so the report can say how long the
    // window was ASKED to be next to how long it turned out to be. They diverge when frames elapsed
    // inside a long event, which the analyzer still measures but the driver's idle counter does not
    // tick down through — and since every mean in the table is divided by the second number, a reader
    // comparing two runs needs to be able to see the divisor moved.
    private static int _requestedFrames;

    public static string? BeginWindow(int requestedFrames)
    {
        if (!IsLoaded)
            return NotLoadedReason;

        try
        {
            AccessTools.Method(Lazy.GUIController, "ResetProfilers").Invoke(null, null);
            _requestedFrames = requestedFrames;
            CountersResetSinceMark = true;
            return null;
        }
        catch (Exception ex)
        {
            return $"failed to reset Dubs Performance Analyzer counters: {Unwrap(ex)}";
        }
    }

    public sealed class Harvest
    {
        public List<ProfileSample> Samples = new List<ProfileSample>();

        // What ProfileMeasure asked for. Zero when the window was bounded by the steps that followed
        // rather than by a frame count, which the report shows as "requested 0, measured N".
        public int RequestedFrames;
        public int MeasuredFrames;
        public double FrameMs;
        public string? Error;

        public static Harvest Failed(string error) => new Harvest { Error = error };
    }

    // Reads the window's statistics out of the analyzer, leaving it running.
    //
    // Split from stopping because run-level profiling harvests at the end of EVERY scenario and must
    // not tear the analyzer down doing it — the next scenario needs the same instrumentation, measured
    // the same way, or the two tables are not comparable and the whole point of a per-scenario table is
    // lost. An explicit Profile step in a run that is not otherwise profiling calls Stop() below.
    public static Harvest ReadWindow()
    {
        if (!IsLoaded)
            return Harvest.Failed(NotLoadedReason);

        try
        {
            return ReadStatistics();
        }
        catch (Exception ex)
        {
            return Harvest.Failed($"failed to read Dubs Performance Analyzer statistics: {Unwrap(ex)}");
        }
    }

    // Harvests and then stops profiling.
    //
    // Deliberately does NOT call Analyzer.Cleanup(): that unpatches everything on a background Task and
    // calls GC.Collect, which is not a thing to do to a run mid-flight. The transplanted method bodies
    // therefore stay for the life of the process, which is exactly what ScenarioResidue.Profiler
    // declares — and, in a run-level session, exactly why the analyzer is started before the first
    // scenario instead: contamination that is uniform across every scenario contaminates none of them
    // relative to the others.
    public static Harvest Stop()
    {
        Harvest harvest = ReadWindow();
        StopProfiling();
        return harvest;
    }

    // Deactivates profiling without reading anything. Called at the end of a run-level session, where
    // the last scenario's harvest already happened.
    public static void StopProfiling()
    {
        if (!IsLoaded || !IsProfiling)
            return;

        try
        {
            EndProfiling();
            IsProfiling = false;
        }
        catch (Exception ex)
        {
            // Logged rather than returned: this runs as the run is being torn down, and there is
            // nothing left to report it to that anyone would act on.
            Log.Warning($"RWTH: failed to stop Dubs Performance Analyzer: {Unwrap(ex)}");
        }
    }

    private static Harvest ReadStatistics()
    {
        int recorded = (int)AccessTools.PropertyGetter(AnalyzerType, "GetCurrentLogCount").Invoke(null, null)!;

        // The analyzer's own statistics pass reads at most RECORDS_HELD-1 of its ring buffer, so a
        // longer window silently becomes "the last 1999 frames". The step spec refuses to ask for one,
        // and this clamp is the belt to that braces.
        int measuredFrames = Math.Min(recorded, ProfileMath.MaxFrames);

        // Returned as an empty harvest rather than read: CollectStatistics divides by this number, so a
        // zero produces NaN averages, and a table of NaNs is both unserializable and indistinguishable
        // from "this mod is free". What an empty window MEANS is not decided here — the pure
        // RunProfiling.AfterWindowSkipReason classifies it, and its callers differ on the disposition
        // (an explicit ProfileStop fails, because the scenario asked to measure and got nothing; a
        // run-level harvest skips, because nobody asked).
        if (measuredFrames <= 0)
            return new Harvest { RequestedFrames = _requestedFrames, MeasuredFrames = 0 };

        Harvest harvest = new Harvest
        {
            RequestedFrames = _requestedFrames,
            MeasuredFrames = measuredFrames,
            FrameMs = Finite(Convert.ToDouble(AccessTools.Field(Lazy.ProfileController, "updateAverage").GetValue(null))),
        };

        MethodInfo collect = AccessTools.Method(Lazy.Profiler, "CollectStatistics");
        FieldInfo empty = AccessTools.Field(Lazy.Profiler, "Empty");
        FieldInfo label = AccessTools.Field(Lazy.Profiler, "label");
        FieldInfo key = AccessTools.Field(Lazy.Profiler, "key");

        foreach (object profiler in Profilers())
        {
            // out-parameters through reflection: the boxed array is written back in place.
            object[] args = { measuredFrames, null!, null!, null!, null!, null! };
            collect.Invoke(profiler, args);

            // CollectStatistics sets Empty when nothing was called in the window, which is how the
            // analyzer's own table drops the thousands of methods that never ran. Reading it AFTER the
            // call is required — it is an output of that call, not a state before it.
            bool ranInWindow = !(bool)empty.GetValue(profiler);
            if (ranInWindow)
            {
                harvest.Samples.Add(new ProfileSample
                {
                    Label = (string?)label.GetValue(profiler) ?? (string?)key.GetValue(profiler) ?? "",
                    AverageMs = Finite(Convert.ToDouble(args[1])),
                    MaxMs = Finite(Convert.ToDouble(args[2])),
                    TotalMs = Finite(Convert.ToDouble(args[3])),
                    Calls = Finite(Convert.ToDouble(args[4])),
                    MaxCallsPerFrame = Finite(Convert.ToDouble(args[5])),
                });
            }
        }

        return harvest;
    }

    // ProfileController.Profiles is a ConcurrentDictionary<string, Profiler>, which we can only see as
    // a non-generic IDictionary from here. Snapshotted into a list first: it is genuinely concurrent
    // and still being written to by the frame we are running inside.
    private static IEnumerable<object> Profilers()
    {
        object? profiles = AccessTools.PropertyGetter(Lazy.ProfileController, "Profiles").Invoke(null, null);
        List<object> snapshot = new List<object>();
        if (profiles is IDictionary dictionary)
        {
            foreach (object? value in dictionary.Values)
            {
                if (value != null)
                    snapshot.Add(value);
            }
        }

        return snapshot;
    }

    private static void EndProfiling()
    {
        AccessTools.Method(AnalyzerType, "EndProfiling").Invoke(null, null);

        if (_activeEntry != null)
        {
            AccessTools.Method(Lazy.Entry, "SetActive").Invoke(_activeEntry, new object[] { false });
            _activeEntry = null;
        }
    }

    // Window_Analyzer.LoadEntries populates the entry list from the [Entry]-attributed types. Normally
    // it runs the first time the window opens; `firstOpen` is the analyzer's own idempotence flag and
    // we honour it rather than inventing a second one.
    private static void LoadEntriesOnce()
    {
        FieldInfo firstOpen = AccessTools.Field(Lazy.Window, "firstOpen");
        if (!(bool)firstOpen.GetValue(null))
            return;

        AccessTools.Method(Lazy.Window, "LoadEntries").Invoke(null, null);
        firstOpen.SetValue(null, false);
    }

    // The two patches that make the analyzer's per-frame measurement cycle run at all: without them
    // ProfileController.BeginUpdate/EndUpdate are never called, no measurement is ever recorded, and
    // every profiler reads empty. Copied from Window_Analyzer.PreOpen, guarded by the analyzer's own
    // Modbase.isPatched so opening its window later does not double-patch.
    private static void PatchUpdateCycleOnce()
    {
        FieldInfo isPatched = AccessTools.Field(Lazy.Modbase, "isPatched");
        if ((bool)isPatched.GetValue(null))
            return;

        // Cast rather than reflected: there is exactly one 0Harmony in the AppDomain (brrainz.harmony
        // provides it, and both we and the analyzer bind to whatever is loaded), so the analyzer's
        // instance is an instance of the very type we compiled against.
        Harmony harmony = (Harmony)AccessTools.PropertyGetter(Lazy.Modbase, "Harmony").Invoke(null, null)!;
        Patch(harmony, AccessTools.Method("Verse.Root_Play:Update"), Lazy.RootUpdate);
        Patch(harmony, AccessTools.Method("Verse.TickManager:DoSingleTick"), Lazy.SingleTick);
        isPatched.SetValue(null, true);
    }

    // Applied through the ANALYZER's own Harmony instance, not ours, so that if its window is opened
    // later (or its cleanup runs) it unpatches what we applied. Patching under our id would leave the
    // analyzer believing it had never patched and double-count every frame.
    private static void Patch(Harmony harmony, MethodBase original, Type? patchClass)
    {
        harmony.Patch(
            original,
            prefix: new HarmonyMethod(AccessTools.Method(patchClass, "Prefix")),
            postfix: new HarmonyMethod(AccessTools.Method(patchClass, "Postfix")));
    }

    // Activates the Harmony-patches entry, which is what actually transplants timing calls into every
    // patched method.
    //
    // Patching is forced SYNCHRONOUS for the duration: the analyzer normally does it on a background
    // Task, and returning before it finished would start the window measuring a load that is still
    // being instrumented — the first N frames would then be missing whichever patches had not been
    // rewritten yet, with nothing anywhere saying so. Rewriting a whole modlist's patches in one frame
    // is slow, which is fine here and would not be in a game someone is playing.
    private static string? ActivateHarmonyPatchesEntry()
    {
        object? entry = FindEntry(HarmonyPatchesEntryTypeName);
        if (entry == null)
            return $"Dubs Performance Analyzer has no '{HarmonyPatchesEntryTypeName}' entry — its API has changed";

        FieldInfo threaded = AccessTools.Field(Lazy.Settings, "disableThreadedPatching");
        bool previous = (bool)threaded.GetValue(null);
        threaded.SetValue(null, true);
        try
        {
            string name = (string)AccessTools.Field(Lazy.Entry, "name").GetValue(entry);
            AccessTools.Method(Lazy.GUIController, "SwapToEntry").Invoke(null, new object[] { name });
        }
        finally
        {
            threaded.SetValue(null, previous);
        }

        // Reported rather than assumed. An entry that failed to patch leaves the analyzer running with
        // nothing instrumented, and the resulting empty table would read as "this mod costs nothing".
        if (!(bool)AccessTools.Field(Lazy.Entry, "isPatched").GetValue(entry))
            return "Dubs Performance Analyzer failed to instrument the Harmony patches (see its own log output)";

        _activeEntry = entry;
        return null;
    }

    // Found by the entry's backing TYPE rather than its name, because the name is run through
    // RimWorld's translation system ("entry.update.harmonypatches".Tr()) and would differ per language.
    //
    // Searched through GUIController's TABS rather than the static Entry.entries list, which cost a
    // live run to learn: Entry.entries only holds entries created at runtime by GUIController.AddEntry
    // (the modder tab's custom ones). The built-in entries are [Entry]-ATTRIBUTED types, and
    // Window_Analyzer.LoadEntries puts those straight into Tab.entries without ever touching
    // Entry.entries. Searching the wrong collection found nothing and reported the analyzer's API as
    // changed.
    //
    // Taking the Entry INSTANCE out of the tab dictionary also matters: RimWorld's attribute lookup
    // hands back a fresh object per call, and the instance the tab holds is the one SwapToEntry
    // activates and the one whose isPatched flag means anything.
    private static object? FindEntry(string typeName)
    {
        FieldInfo entryType = AccessTools.Field(Lazy.Entry, "type");
        FieldInfo tabEntries = AccessTools.Field(Lazy.Tab, "entries");
        object? tabs = AccessTools.PropertyGetter(Lazy.GUIController, "Tabs").Invoke(null, null);

        foreach (object? tab in (IEnumerable)tabs!)
        {
            foreach (object? entry in ((IDictionary)tabEntries.GetValue(tab)).Keys)
            {
                if (entry != null && (entryType.GetValue(entry) as Type)?.Name == typeName)
                    return entry;
            }
        }

        return null;
    }

    // NaN and Infinity are unserializable by System.Text.Json, so one of them anywhere in a table takes
    // out the whole run's report. They arrive from divisions inside the analyzer we do not control.
    private static double Finite(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;

    private static string Unwrap(Exception ex) =>
        (ex is TargetInvocationException invocation && invocation.InnerException != null
            ? invocation.InnerException
            : ex).Message;
}
