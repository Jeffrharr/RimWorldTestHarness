using Mono.Cecil;

namespace RimWorldTestHarness.ApiTests;

// Verifies the Dubs Performance Analyzer API surface Mod/Profiling/DubsAnalyzer.cs drives.
//
// This fixture matters more than the vanilla one it sits beside. Everything the adapter touches is
// reached by reflection over an OPTIONAL Workshop mod, so the compiler checks none of it, and the
// analyzer is not on anyone's release cadence but its own. Without these tests, an analyzer update
// that renames a field surfaces as a scenario reporting "failed to start Dubs Performance Analyzer:
// Object reference not set" — or, worse, as a table of zeroes that reads as "this mod is free".
//
// Ignored rather than failed when the analyzer is not installed: it is optional by design, and a
// machine without it must still be able to run the whole test suite green.
[TestFixture]
[Category("RequiresGameDll")]
public class DubsAnalyzerApiTests
{
    // The Workshop id is the mod's, and 1.6 is the version folder the harness targets. Overridable
    // because a local (non-Steam) copy is a perfectly normal way to have it installed.
    private const string WorkshopAssemblyPath =
        "/home/deck/.local/share/Steam/steamapps/workshop/content/294100/2038874626/1.6/Assemblies/PerformanceAnalyzer.dll";

    private static string AssemblyPath =>
        Environment.GetEnvironmentVariable("DUBS_ANALYZER_ASSEMBLY") ?? WorkshopAssemblyPath;

    private ModuleDefinition _analyzer = null!;

    [OneTimeSetUp]
    public void LoadAssembly()
    {
        if (!File.Exists(AssemblyPath))
        {
            Assert.Ignore(
                $"PerformanceAnalyzer.dll not found at {AssemblyPath} — Dubs Performance Analyzer is " +
                "an optional Workshop mod (2038874626). Set DUBS_ANALYZER_ASSEMBLY to run these tests.");
        }

        _analyzer = ModuleDefinition.ReadModule(AssemblyPath);
    }

    [OneTimeTearDown]
    public void Dispose() => _analyzer?.Dispose();

    // --- Analyzer.Profiling.Analyzer: the on/off switch and the frame counter ---

    [Test]
    public void Analyzer_ProfilingLifecycleMethodsExist()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Analyzer");

        Assert.Multiple(() =>
        {
            AssertMethod(type, "BeginProfiling");
            AssertMethod(type, "EndProfiling");
        });
    }

    // The number of frames the window actually measured. Every mean in a profile table is divided by
    // it, so losing it does not break loudly — it silently changes what the report's numbers mean.
    [Test]
    public void Analyzer_GetCurrentLogCountPropertyExists()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Analyzer");

        Assert.That(type.Properties.Any(p => p.Name == "GetCurrentLogCount"),
                    Is.True, "Analyzer.GetCurrentLogCount is gone — DubsAnalyzer cannot size its window");
    }

    // --- Analyzer.Profiling.Profiler: where the actual numbers come from ---

    // The five out-parameters ARE the profile table. Their order is what DubsAnalyzer's boxed args
    // array indexes into, so a reordering here would silently swap (say) max time and total time and
    // produce a completely plausible wrong report.
    [Test]
    public void Profiler_CollectStatisticsKeepsItsOutParameterOrder()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Profiler");
        MethodDefinition? method = type.Methods.SingleOrDefault(m => m.Name == "CollectStatistics");

        Assert.That(method, Is.Not.Null, "Profiler.CollectStatistics is gone — DubsAnalyzer harvests through it");
        Assert.That(method!.Parameters.Select(p => p.Name),
                    Is.EqualTo(new[] { "entries", "average", "max", "total", "calls", "maxCalls" }),
                    "CollectStatistics's parameters moved — DubsAnalyzer reads them back positionally");
        Assert.That(method.Parameters[0].ParameterType.FullName, Is.EqualTo("System.Int32"));
    }

    // Empty is an OUTPUT of CollectStatistics, and the only thing that distinguishes the handful of
    // methods that ran in the window from the thousands that did not.
    [Test]
    public void Profiler_ExposesEmptyLabelAndKey()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Profiler");

        Assert.Multiple(() =>
        {
            AssertField(type, "Empty");
            AssertField(type, "label");
            AssertField(type, "key");
        });
    }

    // The analyzer's ring buffer size. ProfileMath.MaxFrames is derived from it, and the step spec
    // refuses a longer window because past this point the window silently becomes "the last N frames".
    [Test]
    public void Profiler_RecordsHeldStillMatchesOurFrameCap()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Profiler");
        FieldDefinition? field = type.Fields.SingleOrDefault(f => f.Name == "RECORDS_HELD");

        Assert.That(field, Is.Not.Null, "Profiler.RECORDS_HELD is gone");
        // ProfileMath.MaxFrames is RECORDS_HELD - 1, matching the analyzer's own statistics pass.
        Assert.That(field!.Constant, Is.EqualTo(2000),
                    "the analyzer's ring buffer changed size — update ProfileMath.MaxFrames to match");
    }

    // --- Analyzer.Profiling.ProfileController: the live profiler dictionary and the frame time ---

    [Test]
    public void ProfileController_ExposesProfilesAndUpdateAverage()
    {
        TypeDefinition type = Require("Analyzer.Profiling.ProfileController");

        Assert.Multiple(() =>
        {
            Assert.That(type.Properties.Any(p => p.Name == "Profiles"), Is.True,
                        "ProfileController.Profiles is gone — DubsAnalyzer enumerates it to harvest");
            // The denominator of every percentage in a profile table.
            AssertField(type, "updateAverage");
        });
    }

    // --- Activation: the sequence DubsAnalyzer copies out of Window_Analyzer.PreOpen ---

    [Test]
    public void WindowAnalyzer_LoadEntriesAndFirstOpenExist()
    {
        TypeDefinition type = Require("Analyzer.Window_Analyzer");

        Assert.Multiple(() =>
        {
            AssertMethod(type, "LoadEntries");
            AssertField(type, "firstOpen");
        });
    }

    [Test]
    public void GUIController_SwapToEntryAndResetProfilersExist()
    {
        TypeDefinition type = Require("Analyzer.Profiling.GUIController");

        Assert.Multiple(() =>
        {
            AssertMethod(type, "SwapToEntry");
            AssertMethod(type, "ResetProfilers");
        });
    }

    [Test]
    public void Entry_ExposesTheFieldsWeMatchAndActivateOn()
    {
        TypeDefinition type = Require("Analyzer.Profiling.Entry");

        Assert.Multiple(() =>
        {
            // Matched on `type` rather than `name` because the name is run through RimWorld's
            // translation system and differs per language.
            AssertField(type, "type");
            AssertField(type, "name");
            AssertField(type, "entries");
            AssertField(type, "isPatched");
            AssertMethod(type, "SetActive");
        });
    }

    // The entry that profiles every non-analyzer Harmony patch — the one that attributes cost to mod
    // names rather than to vanilla methods, which is the entire point of the feature.
    [Test]
    public void HarmonyPatchesEntryStillExists()
    {
        Assert.That(_analyzer.Types.Any(t => t.FullName == "Analyzer.Profiling.H_HarmonyPatches"),
                    Is.True,
                    "the Harmony patches entry is gone — there is no per-mod profile table without it");
    }

    // Forced synchronous so the measured window never starts while patching is still in flight.
    [Test]
    public void Settings_DisableThreadedPatchingExists()
    {
        AssertField(Require("Analyzer.Settings"), "disableThreadedPatching");
    }

    // The analyzer's own Harmony instance and the flag guarding its Root_Play.Update patch. DubsAnalyzer
    // patches through both so the analyzer's window and cleanup stay consistent with what we applied.
    [Test]
    public void Modbase_ExposesHarmonyAndIsPatched()
    {
        TypeDefinition type = Require("Analyzer.Modbase");

        Assert.Multiple(() =>
        {
            Assert.That(type.Properties.Any(p => p.Name == "Harmony"), Is.True, "Modbase.Harmony is gone");
            AssertField(type, "isPatched");
        });
    }

    [TestCase("Analyzer.Profiling.H_RootUpdate")]
    [TestCase("Analyzer.Profiling.H_DoSingleTickUpdate")]
    public void MeasurementCyclePatchesExposePrefixAndPostfix(string fullName)
    {
        TypeDefinition type = Require(fullName);

        Assert.Multiple(() =>
        {
            AssertMethod(type, "Prefix");
            AssertMethod(type, "Postfix");
        });
    }

    private TypeDefinition Require(string fullName)
    {
        TypeDefinition? type = _analyzer.Types.FirstOrDefault(t => t.FullName == fullName);
        Assert.That(type, Is.Not.Null, $"{fullName} no longer exists in PerformanceAnalyzer.dll");
        return type!;
    }

    private static void AssertMethod(TypeDefinition type, string name) =>
        Assert.That(type.Methods.Any(m => m.Name == name), Is.True,
                    $"{type.FullName}.{name} no longer exists — DubsAnalyzer calls it by reflection");

    private static void AssertField(TypeDefinition type, string name) =>
        Assert.That(type.Fields.Any(f => f.Name == name), Is.True,
                    $"{type.FullName}.{name} no longer exists — DubsAnalyzer reads it by reflection");
}
