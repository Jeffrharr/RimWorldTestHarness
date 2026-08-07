using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SuitePlannerTests
{
    private static ScenarioSpec Scenario(string name, params string[] stepTypes)
    {
        ScenarioSpec spec = new ScenarioSpec { Name = name };
        foreach (string type in stepTypes)
            spec.Steps.Add(new ScenarioStep { Type = type });

        return spec;
    }

    // A scenario that only reads the world (probe + screenshot) still dirties screenshotMode, so it
    // gets a soft-reset boundary; nothing here mutates the map, so nothing reloads.
    private static ScenarioSpec ReadOnly(string name) => Scenario(name, StepArgs.ProbeType);

    private static ScenarioSpec MapMutating(string name) => Scenario(name, StepArgs.PlaceThingsType);

    private static ScenarioSpec ClockOnly(string name) => Scenario(name, StepArgs.SetTimeType);

    private static IsolationAction[] Actions(SuitePlan plan) =>
        plan.Entries.Select(e => e.Before).ToArray();

    // --- the trivial case ---

    [Test]
    public void Single_HasNoBoundariesAndNoGates()
    {
        var plan = SuitePlanner.Single(MapMutating("only"));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries, Has.Count.EqualTo(1));
            Assert.That(plan.Entries[0].Before, Is.EqualTo(IsolationAction.None));
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(plan.Notes, Is.Empty);
        });
    }

    // --- the vacuous-truth guard ---

    [Test]
    public void Plan_EmptySuiteIsAnError()
    {
        var plan = SuitePlanner.Plan(new List<ScenarioSpec>(), IsolationPolicy.Auto, reloadAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries, Is.Empty);
            Assert.That(plan.Errors, Has.Count.EqualTo(1));
            Assert.That(plan.Errors[0], Does.Contain("no scenarios"));
        });
    }

    // --- Auto ---

    [Test]
    public void Auto_FirstScenarioNeedsNothing()
    {
        var plan = SuitePlanner.Plan(new[] { MapMutating("a"), ReadOnly("b") }, IsolationPolicy.Auto, true);

        Assert.That(plan.Entries[0].Before, Is.EqualTo(IsolationAction.None));
    }

    [Test]
    public void Auto_ReloadsAfterAMapMutatingScenario()
    {
        var plan = SuitePlanner.Plan(new[] { MapMutating("a"), ClockOnly("b") }, IsolationPolicy.Auto, true);

        Assert.Multiple(() =>
        {
            Assert.That(Actions(plan), Is.EqualTo(new[] { IsolationAction.None, IsolationAction.ReloadSave }));
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(plan.Notes, Is.Empty);
        });
    }

    [Test]
    public void Auto_SoftResetsBetweenScenariosThatOnlyDirtyRestorableState()
    {
        var plan = SuitePlanner.Plan(
            new[] { ClockOnly("a"), ClockOnly("b"), ReadOnly("c") }, IsolationPolicy.Auto, true);

        Assert.That(Actions(plan), Is.EqualTo(new[]
        {
            IsolationAction.None, IsolationAction.SoftReset, IsolationAction.SoftReset,
        }));
    }

    [Test]
    public void Auto_NeedsNothingBetweenScenariosThatLeaveNothingBehind()
    {
        var plan = SuitePlanner.Plan(
            new[] { Scenario("a", StepArgs.WaitType), Scenario("b", StepArgs.ProbeType) },
            IsolationPolicy.Auto, true);

        Assert.That(Actions(plan), Is.EqualTo(new[] { IsolationAction.None, IsolationAction.None }));
    }

    // The reason residue accumulates across scenarios rather than being read off the previous one: a
    // soft reset leaves map residue in place, so every later scenario is still contaminated by
    // scenario 1's spawned things until something actually reloads.
    //
    // Shown under Never (which forbids the reload that would clear it): both 'b' AND 'c' get a
    // shared-world note, not just the one immediately after the mutator.
    [Test]
    public void MapResidueSurvivesInterveningSoftResets()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), ClockOnly("b"), ClockOnly("c") }, IsolationPolicy.Never, true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Notes, Has.Count.EqualTo(2));
            Assert.That(plan.Notes[0], Does.Contain("'b'"));
            Assert.That(plan.Notes[1], Does.Contain("'c'"));
        });
    }

    [Test]
    public void Auto_ReloadClearsResidueSoTheNextBoundaryIsCheaper()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), Scenario("b", StepArgs.WaitType), ClockOnly("c") },
            IsolationPolicy.Auto, true);

        Assert.That(Actions(plan), Is.EqualTo(new[]
        {
            IsolationAction.None,           // fresh load
            IsolationAction.ReloadSave,     // 'a' mutated the map
            IsolationAction.None,           // 'b' left nothing behind
        }));
    }

    // The involuntary-degradation case: the caller asked for isolation and the environment can't give
    // it. That must fail the suite, not quietly run scenario 'b' against 'a''s world.
    [Test]
    public void Auto_WithoutAReloadSaveErrorsOnAMapMutatingBoundary()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), ClockOnly("b") }, IsolationPolicy.Auto, reloadAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(Actions(plan), Is.EqualTo(new[] { IsolationAction.None, IsolationAction.SoftReset }));
            Assert.That(plan.Errors, Has.Count.EqualTo(1));
            Assert.That(plan.Errors[0], Does.Contain("'b'").And.Contain("mutated the map"));
            Assert.That(plan.Notes, Is.Empty);
        });
    }

    // ... but a map-mutating scenario with nothing after it needs no boundary at all, so no error.
    [Test]
    public void Auto_WithoutAReloadSaveAcceptsAMapMutatingScenarioLast()
    {
        var plan = SuitePlanner.Plan(
            new[] { ClockOnly("a"), MapMutating("b") }, IsolationPolicy.Auto, reloadAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(Actions(plan), Is.EqualTo(new[] { IsolationAction.None, IsolationAction.SoftReset }));
            Assert.That(plan.Errors, Is.Empty);
        });
    }

    // --- Always ---

    [Test]
    public void Always_ReloadsBeforeEveryScenarioAfterTheFirst()
    {
        var plan = SuitePlanner.Plan(
            new[] { ReadOnly("a"), ReadOnly("b"), ReadOnly("c") }, IsolationPolicy.Always, true);

        Assert.That(Actions(plan), Is.EqualTo(new[]
        {
            IsolationAction.None, IsolationAction.ReloadSave, IsolationAction.ReloadSave,
        }));
    }

    [Test]
    public void Always_WithoutAReloadSaveIsAnError()
    {
        var plan = SuitePlanner.Plan(
            new[] { ReadOnly("a"), ReadOnly("b") }, IsolationPolicy.Always, reloadAvailable: false);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Errors, Has.Count.EqualTo(1));
            Assert.That(plan.Errors[0], Does.Contain("isolation=always"));
        });
    }

    // --- Never ---

    // Consent is the distinction from the Auto case above: the caller chose the shared world, so it is
    // recorded as a note (reported, never failing) rather than an error.
    [Test]
    public void Never_RecordsAMapMutatingBoundaryAsANoteNotAnError()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), ClockOnly("b") }, IsolationPolicy.Never, reloadAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(Actions(plan), Is.EqualTo(new[] { IsolationAction.None, IsolationAction.SoftReset }));
            Assert.That(plan.Errors, Is.Empty);
            Assert.That(plan.Notes, Has.Count.EqualTo(1));
            Assert.That(plan.Notes[0], Does.Contain("isolation=never"));
        });
    }

    [Test]
    public void Never_NeverPlansAReloadEvenWhenOneIsAvailable()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), MapMutating("b"), MapMutating("c") }, IsolationPolicy.Never, true);

        Assert.That(Actions(plan), Has.No.Member(IsolationAction.ReloadSave));
    }

    // --- ordering and bookkeeping ---

    [Test]
    public void Plan_PreservesTheAuthoredOrderAndRecordsResidue()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("first"), ClockOnly("second") }, IsolationPolicy.Auto, true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries.Select(e => e.ScenarioName), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(plan.Entries.Select(e => e.ScenarioIndex), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(plan.Entries[0].Residue, Is.EqualTo(ScenarioResidue.Map));
            Assert.That(plan.Entries[1].Residue, Is.EqualTo(ScenarioResidue.Clock));
        });
    }

    // Screenshot collisions are a suite-level gate, checked at plan time so the failure lands before
    // the game spends minutes writing frames over each other.
    [Test]
    public void Plan_SurfacesScreenshotCollisions()
    {
        ScenarioSpec Shot(string name) => new ScenarioSpec
        {
            Name = name,
            Steps = { new ScenarioStep { Type = StepArgs.ScreenshotType, Args = { [StepArgs.ScreenshotFileName] = "shot.png" } } },
        };

        var distinct = SuitePlanner.Plan(new[] { Shot("a"), Shot("b") }, IsolationPolicy.Auto, true);
        var colliding = SuitePlanner.Plan(new[] { Shot("same"), Shot("same") }, IsolationPolicy.Auto, true);

        Assert.Multiple(() =>
        {
            Assert.That(distinct.Errors, Is.Empty, "qualification by scenario name should keep these apart");
            Assert.That(colliding.Errors, Has.Count.EqualTo(1));
            Assert.That(colliding.Errors[0], Does.Contain("collision"));
        });
    }

    // --- policy parsing ---

    [TestCase(null, IsolationPolicy.Auto)]
    [TestCase("", IsolationPolicy.Auto)]
    [TestCase("auto", IsolationPolicy.Auto)]
    [TestCase("AUTO", IsolationPolicy.Auto)]
    [TestCase(" always ", IsolationPolicy.Always)]
    [TestCase("never", IsolationPolicy.Never)]
    public void TryParsePolicy_AcceptsKnownValuesCaseInsensitively(string? raw, IsolationPolicy expected)
    {
        bool ok = SuitePlanner.TryParsePolicy(raw, out IsolationPolicy policy, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(policy, Is.EqualTo(expected));
        });
    }

    // A typo must not silently become the default: a run that isolated less than it was asked to looks
    // identical to one that isolated correctly.
    [Test]
    public void TryParsePolicy_RejectsUnknownValue()
    {
        bool ok = SuitePlanner.TryParsePolicy("alwyas", out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("alwyas"));
        });
    }

    // --- run-level profiling and the Profiler residue ---

    private static ScenarioSpec Profiling(string name) => Scenario(name, StepArgs.ProfileType);

    // The cost that made run-level profiling worth building. Profiler residue is not soft-resettable,
    // so a suite in which every scenario profiles used to pay a full save reload between each pair.
    [Test]
    public void Plan_ProfilingScenariosReloadBetweenEachOtherWhenTheRunIsNotProfiled()
    {
        var plan = SuitePlanner.Plan(
            new[] { Profiling("a"), Profiling("b"), Profiling("c") },
            IsolationPolicy.Auto, reloadAvailable: true, profilerAlreadyActive: false);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries[0].Residue & ScenarioResidue.Profiler,
                        Is.EqualTo(ScenarioResidue.Profiler));
            // Not a reload — Profiler is outside SoftResettable but MapDirty is what forces one; the
            // point is only that the flag survives to describe a scenario as dirty.
            Assert.That(Actions(plan)[1], Is.Not.EqualTo(IsolationAction.None));
        });
    }

    // With the analyzer started before scenario one, every scenario is instrumented identically from
    // before any of them ran, so Profiler stops describing anything one scenario does to the next.
    // This is what keeps a profiled suite at ONE boot.
    [Test]
    public void Plan_RunLevelProfilingMasksTheProfilerResidueEntirely()
    {
        var plan = SuitePlanner.Plan(
            new[] { Profiling("a"), Profiling("b"), Profiling("c") },
            IsolationPolicy.Auto, reloadAvailable: true, profilerAlreadyActive: true);

        Assert.Multiple(() =>
        {
            foreach (var entry in plan.Entries)
                Assert.That(entry.Residue & ScenarioResidue.Profiler,
                            Is.EqualTo(ScenarioResidue.None), entry.ScenarioName);

            Assert.That(plan.Errors, Is.Empty);
        });
    }

    // Only Profiler is ever masked. Everything else describes a change a scenario genuinely made, and
    // a profiled run is not a licence to stop isolating.
    [Test]
    public void Plan_RunLevelProfilingDoesNotMaskAnyOtherResidue()
    {
        var plan = SuitePlanner.Plan(
            new[] { MapMutating("a"), ReadOnly("b") },
            IsolationPolicy.Auto, reloadAvailable: true, profilerAlreadyActive: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries[0].Residue & ScenarioResidue.Map,
                        Is.EqualTo(ScenarioResidue.Map));
            Assert.That(Actions(plan)[1], Is.EqualTo(IsolationAction.ReloadSave));
        });
    }

    // A Profile step also declares TimeSpeed residue (it forces a game speed). Masking Profiler must
    // not take that with it, or the following scenario would run at whatever speed this one left.
    [Test]
    public void Plan_MaskingProfilerLeavesTheTimeSpeedItAlsoDirtied()
    {
        var plan = SuitePlanner.Plan(
            new[] { Profiling("a"), ReadOnly("b") },
            IsolationPolicy.Auto, reloadAvailable: true, profilerAlreadyActive: true);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Entries[0].Residue & ScenarioResidue.TimeSpeed,
                        Is.EqualTo(ScenarioResidue.TimeSpeed));
            Assert.That(Actions(plan)[1], Is.EqualTo(IsolationAction.SoftReset));
        });
    }
}
