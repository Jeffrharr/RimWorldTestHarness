using System.Text.Json;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SuiteReportTests
{
    private static ScenarioReport Scenario(string name, bool pass, params string[] errors)
    {
        ScenarioReport report = new ScenarioReport { ScenarioName = name, Pass = pass };
        report.Errors.AddRange(errors);
        return report;
    }

    private static SuiteReport Suite(params ScenarioReport[] scenarios)
    {
        SuiteReport suite = new SuiteReport();
        suite.Scenarios.AddRange(scenarios);
        return suite;
    }

    // --- the suite gate ---

    [Test]
    public void AllPass_TrueOnlyWhenEveryScenarioPassed()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ReportComparer.AllPass(Suite(Scenario("a", true), Scenario("b", true))), Is.True);
            Assert.That(ReportComparer.AllPass(Suite(Scenario("a", true), Scenario("b", false))), Is.False);
        });
    }

    // The same vacuous-truth bug as AllPass over an empty probe-check list, one level up: a suite that
    // ran nothing must not report success.
    [Test]
    public void AllPass_EmptySuiteDoesNotPass()
    {
        Assert.That(ReportComparer.AllPass(new SuiteReport()), Is.False);
    }

    // A reload that never completed, an unparsable suite list or a screenshot collision invalidates the
    // run as a whole, even if every scenario that did run passed.
    [Test]
    public void AllPass_SuiteLevelErrorFailsEvenWhenEveryScenarioPassed()
    {
        SuiteReport suite = Suite(Scenario("a", true), Scenario("b", true));
        suite.Errors.Add("mid-suite reload failed");

        Assert.That(ReportComparer.AllPass(suite), Is.False);
    }

    // Isolation notes are the consented-shortfall channel, deliberately NOT part of the gate.
    [Test]
    public void AllPass_IsolationNotesDoNotAffectPass()
    {
        SuiteReport suite = Suite(Scenario("a", true));
        suite.IsolationNotes.Add("ran against a mutated world as requested");

        Assert.That(ReportComparer.AllPass(suite), Is.True);
    }

    // A scenario the suite never reached carries SuiteReportBuilder.NotRunReason, which fails it through
    // the per-scenario gate — so an abort can't shrink a suite into a green one.
    [Test]
    public void NotRunScenarioFailsThroughThePerScenarioGate()
    {
        ScenarioReport unreached = Scenario("c", false, SuiteReportBuilder.NotRunReason);

        Assert.Multiple(() =>
        {
            Assert.That(ReportComparer.AllPass(unreached.ProbeChecks, unreached.Errors), Is.False);
            Assert.That(ReportComparer.AllPass(Suite(Scenario("a", true), unreached)), Is.False);
        });
    }

    // --- report construction ---

    [Test]
    public void ForScenario_CarriesLoadErrorsIntoTheReport()
    {
        ScenarioSpec spec = new ScenarioSpec { Name = "x" };
        spec.LoadErrors.Add("step 2 is invalid");

        ScenarioReport report = SuiteReportBuilder.ForScenario(spec);

        Assert.Multiple(() =>
        {
            Assert.That(report.ScenarioName, Is.EqualTo("x"));
            Assert.That(report.Errors, Is.EqualTo(new[] { "step 2 is invalid" }));
        });
    }

    // --- serialization shape ---

    // Runner/run_test.sh's Step 7 gate and anything else reading Runner/reports/*.json know the bare
    // single-scenario shape. A single run must keep producing exactly that, with no wrapper key.
    [Test]
    public void Serialize_SingleRunWritesTheBareScenarioShape()
    {
        SuiteReport suite = Suite(Scenario("solo", true));
        suite.Scenarios[0].ScreenshotPaths.Add("/reports/shot.png");

        string json = SuiteReportSerializer.Serialize(suite, suiteMode: false);

        Assert.Multiple(() =>
        {
            Assert.That(json, Does.Not.Contain("\"Scenarios\""));
            Assert.That(json, Does.Contain("\"ScenarioName\":\"solo\""));
            Assert.That(json, Does.Contain("\"Pass\":true"));
            Assert.That(json, Does.Contain("\"ScreenshotPaths\""));
        });
    }

    // Keyed off the launch mode, not the scenario count, so the runner always knows which shape to
    // expect rather than having to guess.
    [Test]
    public void Serialize_SuiteRunWritesTheWrapperEvenForOneScenario()
    {
        SuiteReport suite = Suite(Scenario("solo", true));
        suite.Pass = true;

        string json = SuiteReportSerializer.Serialize(suite, suiteMode: true);

        Assert.That(json, Does.Contain("\"Scenarios\""));
    }

    // PascalCase with no naming policy — run_test.sh's inline Python reads the C# property names
    // literally, so a policy added anywhere in this chain would silently break the gate.
    [Test]
    public void Serialize_SuiteRoundTripsThroughPascalCaseKeys()
    {
        SuiteReport suite = Suite(Scenario("a", true), Scenario("b", false, "boom"));
        suite.Errors.Add("suite level");
        suite.IsolationNotes.Add("a note");
        suite.Pass = false;

        SuiteReport? back = JsonSerializer.Deserialize<SuiteReport>(
            SuiteReportSerializer.Serialize(suite, suiteMode: true));

        Assert.That(back, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(back!.Pass, Is.False);
            Assert.That(back.Scenarios.Select(s => s.ScenarioName), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(back.Scenarios[1].Errors, Is.EqualTo(new[] { "boom" }));
            Assert.That(back.Errors, Is.EqualTo(new[] { "suite level" }));
            Assert.That(back.IsolationNotes, Is.EqualTo(new[] { "a note" }));
        });
    }
}
