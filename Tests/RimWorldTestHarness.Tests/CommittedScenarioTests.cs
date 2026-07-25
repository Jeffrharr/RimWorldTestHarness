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
}
