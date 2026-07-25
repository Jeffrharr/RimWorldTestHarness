using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Covers the one part of suite loading that genuinely needs a filesystem: resolving a suite list's
// entries and turning an unreadable scenario into a visible failure rather than a shorter suite.
[TestFixture]
public class SuiteSpecLoaderTests
{
    private string _dir = "";

    [SetUp]
    public void CreateTempDir()
    {
        _dir = Path.Combine(Path.GetTempPath(), "rwth-suite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void DeleteTempDir()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string WriteScenario(string fileName, string name)
    {
        string path = Path.Combine(_dir, fileName);
        File.WriteAllText(path,
            """{"name":"NAME","saveFile":"f.rws","steps":[{"type":"Wait","args":{"frames":"1"}}]}"""
                .Replace("NAME", name));
        return path;
    }

    private string WriteList(string contents)
    {
        string path = Path.Combine(_dir, "suite.txt");
        File.WriteAllText(path, contents);
        return path;
    }

    [Test]
    public void LoadSuiteFromFile_LoadsEveryListedScenarioInOrder()
    {
        WriteScenario("a.json", "alpha");
        WriteScenario("b.json", "beta");
        string list = WriteList("b.json\na.json\n");

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(list);

        Assert.Multiple(() =>
        {
            Assert.That(suite.LoadErrors, Is.Empty);
            Assert.That(suite.Scenarios.Select(s => s.Name), Is.EqualTo(new[] { "beta", "alpha" }));
            Assert.That(suite.Scenarios[0].Steps, Has.Count.EqualTo(1));
        });
    }

    // A missing or unparsable scenario must not shrink the suite: it becomes a named, step-less
    // placeholder so it still shows up in the report as a scenario that failed.
    [Test]
    public void LoadSuiteFromFile_MissingScenarioBecomesAFailingPlaceholder()
    {
        WriteScenario("a.json", "alpha");
        string list = WriteList("a.json\nnope.json\n");

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(list);

        Assert.Multiple(() =>
        {
            Assert.That(suite.Scenarios, Has.Count.EqualTo(2), "the missing scenario must still be listed");
            Assert.That(suite.Scenarios[1].Name, Is.EqualTo("nope"));
            Assert.That(suite.Scenarios[1].Steps, Is.Empty);
            Assert.That(suite.Scenarios[1].LoadErrors, Has.Count.EqualTo(1));
            Assert.That(suite.LoadErrors, Has.Count.EqualTo(1));
            Assert.That(suite.LoadErrors[0], Does.Contain("nope.json"));
        });
    }

    [Test]
    public void LoadSuiteFromFile_MalformedJsonBecomesAFailingPlaceholder()
    {
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ this is not json");
        string list = WriteList("bad.json\n");

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(list);

        Assert.Multiple(() =>
        {
            Assert.That(suite.Scenarios, Has.Count.EqualTo(1));
            Assert.That(suite.Scenarios[0].LoadErrors, Is.Not.Empty);
            Assert.That(suite.LoadErrors, Is.Not.Empty);
        });
    }

    [Test]
    public void LoadSuiteFromFile_EmptyListIsAnError()
    {
        string list = WriteList("# nothing here\n");

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(list);

        Assert.Multiple(() =>
        {
            Assert.That(suite.Scenarios, Is.Empty);
            Assert.That(suite.LoadErrors, Has.Count.EqualTo(1));
        });
    }

    // Timelapse desugaring runs per scenario at the usual choke point, so a suite sees primitive steps
    // (and therefore real per-frame screenshot names for the collision check).
    [Test]
    public void LoadSuiteFromFile_ExpandsCompositeStepsPerScenario()
    {
        File.WriteAllText(Path.Combine(_dir, "tl.json"), """
            {"name":"tl","saveFile":"f.rws","steps":[
              {"type":"Timelapse","args":{"fromHour":"0","toHour":"3","stepHours":"1","fileNamePrefix":"tl"}}]}
            """);
        string list = WriteList("tl.json\n");

        SuiteSpec suite = ScenarioSpecLoader.LoadSuiteFromFile(list);

        Assert.Multiple(() =>
        {
            Assert.That(suite.LoadErrors, Is.Empty);
            Assert.That(SuiteScreenshots.FileNamesOf(suite.Scenarios[0]),
                Is.EqualTo(new[] { "tl_0000.png", "tl_0001.png", "tl_0002.png" }));
        });
    }
}
