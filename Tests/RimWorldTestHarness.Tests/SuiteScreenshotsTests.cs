using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SuiteScreenshotsTests
{
    private static ScenarioSpec WithShots(string name, params string[] fileNames)
    {
        ScenarioSpec spec = new ScenarioSpec { Name = name };
        foreach (string fileName in fileNames)
        {
            spec.Steps.Add(new ScenarioStep
            {
                Type = StepArgs.ScreenshotType,
                Args = { [StepArgs.ScreenshotFileName] = fileName },
            });
        }

        return spec;
    }

    // Runner/run_test.sh mirrors this rule in bash to locate a suite's timelapse frames for ffmpeg, so
    // the exact spelling is a cross-language contract. Changing it means changing both sides.
    [Test]
    public void QualifySpellingIsPinned()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SuiteScreenshots.Separator, Is.EqualTo("__"));
            Assert.That(SuiteScreenshots.Qualify("daycycle_timelapse", "daycycle_0007.png"),
                Is.EqualTo("daycycle_timelapse__daycycle_0007.png"));
        });
    }

    [TestCase("plain_name", ExpectedResult = "plain_name")]
    [TestCase("with-dash.and.dot", ExpectedResult = "with-dash.and.dot")]
    [TestCase("with space", ExpectedResult = "with_space")]
    [TestCase("with/slash", ExpectedResult = "with_slash")]
    [TestCase("quote'and\"quote", ExpectedResult = "quote_and_quote")]
    [TestCase("glob*?[chars]", ExpectedResult = "glob___chars_")]
    [TestCase("MixedCase123", ExpectedResult = "MixedCase123")]
    public string PrefixFor_SanitizesToShellSafeCharacters(string scenarioName) =>
        SuiteScreenshots.PrefixFor(scenarioName);

    // An all-unsafe or empty name would otherwise become a prefix of bare underscores (or nothing),
    // which reads as a corrupted filename rather than a scenario's output.
    [TestCase("")]
    public void PrefixFor_FallsBackForAnEmptyName(string scenarioName)
    {
        Assert.That(SuiteScreenshots.PrefixFor(scenarioName), Is.EqualTo("scenario"));
    }

    [Test]
    public void FileNamesOf_CollectsScreenshotStepsInOrderAndIgnoresOthers()
    {
        ScenarioSpec spec = WithShots("s", "one.png", "two.png");
        spec.Steps.Insert(1, new ScenarioStep { Type = StepArgs.WaitType });
        // A Screenshot step missing its fileName arg is a StepValidator/executor problem, not this
        // one's — it must not throw here.
        spec.Steps.Add(new ScenarioStep { Type = StepArgs.ScreenshotType });

        Assert.That(SuiteScreenshots.FileNamesOf(spec), Is.EqualTo(new[] { "one.png", "two.png" }));
    }

    // The whole point of qualification: two scenarios independently authored with the same filename
    // must not fight over one file.
    [Test]
    public void CollisionErrors_SameFileNameInDifferentScenariosIsFine()
    {
        var errors = SuiteScreenshots.CollisionErrors(new[] { WithShots("a", "shot.png"), WithShots("b", "shot.png") });

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void CollisionErrors_SameFileNameTwiceInOneScenarioIsReported()
    {
        var errors = SuiteScreenshots.CollisionErrors(new[] { WithShots("a", "shot.png", "shot.png") });

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("a__shot.png").And.Contain("'a'"));
        });
    }

    // The residual case qualification cannot fix: two distinct scenario names that sanitize to the
    // same prefix. CommittedScenarioTests already forbids duplicate names, but nothing forbids
    // "my scene" and "my/scene".
    [Test]
    public void CollisionErrors_ScenarioNamesThatSanitizeAlikeAreReported()
    {
        var errors = SuiteScreenshots.CollisionErrors(new[] { WithShots("my scene", "shot.png"), WithShots("my/scene", "shot.png") });

        Assert.Multiple(() =>
        {
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("my_scene__shot.png"));
        });
    }

    [Test]
    public void CollisionErrors_ReportsEveryCollisionNotJustTheFirst()
    {
        var errors = SuiteScreenshots.CollisionErrors(new[] { WithShots("a", "one.png", "one.png", "two.png", "two.png") });

        Assert.That(errors, Has.Count.EqualTo(2));
    }
}
