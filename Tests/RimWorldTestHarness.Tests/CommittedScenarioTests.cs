using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Loads every scenario committed under Scenarios/ through the real loader and asserts it produces no
// LoadErrors. The other fixtures test the loader against inline JSON; this one tests the files we
// actually ship, so a typo in a committed scenario fails here instead of after a multi-minute game
// boot. Cheap to keep green, and it grows automatically as scenarios are added.
[TestFixture]
public class CommittedScenarioTests
{
    private static string ScenariosDir()
    {
        // Walk up from the test assembly to the repo root rather than hardcoding a depth, so this
        // survives a TargetFramework or configuration change in the csproj.
        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Scenarios")))
            dir = dir.Parent;

        Assert.That(dir, Is.Not.Null, "could not locate the Scenarios/ directory above the test assembly");
        return Path.Combine(dir!.FullName, "Scenarios");
    }

    private static IEnumerable<string> ScenarioFiles() =>
        Directory.EnumerateFiles(ScenariosDir(), "*.json").OrderBy(p => p);

    [Test]
    public void ScenariosDirectory_IsNotEmpty()
    {
        // Guards against the walk above finding a wrong-but-existing Scenarios/ dir, which would make
        // the per-file test below vacuously pass.
        Assert.That(ScenarioFiles(), Is.Not.Empty);
    }

    [TestCaseSource(nameof(ScenarioFiles))]
    public void CommittedScenario_LoadsWithoutErrors(string path)
    {
        ScenarioSpec spec = ScenarioSpecLoader.LoadFromFile(path);

        Assert.Multiple(() =>
        {
            Assert.That(spec.LoadErrors, Is.Empty,
                $"{Path.GetFileName(path)} produced load errors: {string.Join("; ", spec.LoadErrors)}");
            Assert.That(spec.Name, Is.Not.Empty, $"{Path.GetFileName(path)} has no name");
            Assert.That(spec.Steps, Is.Not.Empty, $"{Path.GetFileName(path)} has no steps");
        });
    }

    // A scenario's name is what run_test.sh logs and what the report is keyed by, so a copy-paste
    // that leaves two files claiming the same name makes two runs indistinguishable.
    [Test]
    public void CommittedScenarios_HaveDistinctNames()
    {
        var names = ScenarioFiles().Select(p => ScenarioSpecLoader.LoadFromFile(p).Name).ToList();

        Assert.That(names, Is.Unique);
    }

    // Committed screenshot names must not collide across the scenarios we ship, or a suite containing
    // them would fail at plan time — better to learn that here than after a multi-minute boot.
    [Test]
    public void CommittedScenarios_HaveNoScreenshotNameCollisions()
    {
        var specs = ScenarioFiles().Select(ScenarioSpecLoader.LoadFromFile).ToList();

        Assert.That(SuiteScreenshots.CollisionErrors(specs), Is.Empty);
    }

    private static IEnumerable<string> SuiteFiles() =>
        Directory.EnumerateFiles(ScenariosDir(), "*.txt").OrderBy(p => p);

    // The suite-list equivalent of the per-scenario test above: a committed suite whose entries moved
    // or whose scenarios stopped agreeing on a fixture should fail offline, not at boot. Vacuously
    // passes when no suite files are committed, which is fine — the point is that any we do ship stay
    // runnable.
    [TestCaseSource(nameof(SuiteFiles))]
    public void CommittedSuite_LoadsAndPlansWithoutErrors(string path)
    {
        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(path);
        // reloadAvailable: true because a committed suite is meant to be run against its fixture; the
        // no-fixture (-quicktest) case is a property of how it's launched, not of the file.
        SuitePlan plan = SuitePlanner.Plan(suite.Scenarios, IsolationPolicy.Auto, reloadAvailable: true);

        Assert.Multiple(() =>
        {
            Assert.That(suite.LoadErrors, Is.Empty,
                $"{Path.GetFileName(path)} produced load errors: {string.Join("; ", suite.LoadErrors)}");
            Assert.That(plan.Errors, Is.Empty,
                $"{Path.GetFileName(path)} does not plan cleanly: {string.Join("; ", plan.Errors)}");
            // Runner/run_test.sh rejects a suite whose scenarios name different fixtures, since the save
            // is installed once at boot. Caught here too, because that rejection costs a launch.
            Assert.That(suite.Scenarios.Select(s => s.SaveFile).Distinct().Count(), Is.EqualTo(1),
                $"{Path.GetFileName(path)} mixes saveFiles — run_test.sh will refuse it");
        });
    }
}
