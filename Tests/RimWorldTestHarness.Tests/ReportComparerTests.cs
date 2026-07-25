using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class ReportComparerTests
{
    [TestCase(1.0f, 1.0f, 0.0f, ExpectedResult = true)]
    [TestCase(1.0f, 1.05f, 0.1f, ExpectedResult = true)]
    [TestCase(1.0f, 1.2f, 0.1f, ExpectedResult = false)]
    [TestCase(-1.0f, -1.05f, 0.1f, ExpectedResult = true)]
    [TestCase(1.0f, 1.05f, -0.1f, ExpectedResult = true)] // negative tolerance treated as its magnitude
    public bool WithinTolerance_MatchesExpected(float actual, float expected, float tolerance) =>
        ReportComparer.WithinTolerance(actual, expected, tolerance);

    [Test]
    public void WithinTolerance_ExactBoundary_Passes()
    {
        // Deliberately not (1.0f, 1.1f, 0.1f) — 1.1f - 1.0f isn't exactly 0.1f in float, which
        // would make this a flaky float-precision test rather than a real boundary check.
        Assert.That(ReportComparer.WithinTolerance(1.0f, 1.25f, 0.25f), Is.True);
    }

    [Test]
    public void CheckProbe_PopulatesAllFieldsAndPassFlag()
    {
        ProbeCheckResult result = ReportComparer.CheckProbe("shadow_lean", actual: 0.42f, expected: 0.4f, tolerance: 0.05f);

        Assert.That(result.ProbeName, Is.EqualTo("shadow_lean"));
        Assert.That(result.ActualValue, Is.EqualTo(0.42f));
        Assert.That(result.ExpectedValue, Is.EqualTo(0.4f));
        Assert.That(result.Tolerance, Is.EqualTo(0.05f));
        Assert.That(result.Pass, Is.True);
    }

    [Test]
    public void AllPass_EmptyList_IsTrue()
    {
        Assert.That(ReportComparer.AllPass(new List<ProbeCheckResult>()), Is.True);
    }

    [Test]
    public void AllPass_AllPassing_IsTrue()
    {
        var checks = new List<ProbeCheckResult>
        {
            ReportComparer.CheckProbe("a", 1f, 1f, 0f),
            ReportComparer.CheckProbe("b", 2f, 2.01f, 0.05f),
        };

        Assert.That(ReportComparer.AllPass(checks), Is.True);
    }

    [Test]
    public void AllPass_OneFailing_IsFalse()
    {
        var checks = new List<ProbeCheckResult>
        {
            ReportComparer.CheckProbe("a", 1f, 1f, 0f),
            ReportComparer.CheckProbe("b", 2f, 5f, 0.05f),
        };

        Assert.That(ReportComparer.AllPass(checks), Is.False);
    }

    // The errors-aware overload is the gate a run actually uses. The empty-checks case is the one
    // that matters: a scenario with no Probe step at all (a pure screenshot or timelapse run) would
    // otherwise report Pass no matter how many of its steps blew up.
    [Test]
    public void AllPass_NoChecksButErrors_IsFalse()
    {
        Assert.That(
            ReportComparer.AllPass(new List<ProbeCheckResult>(), new List<string> { "PlaceThings: boom" }),
            Is.False);
    }

    [Test]
    public void AllPass_PassingChecksButErrors_IsFalse()
    {
        var checks = new List<ProbeCheckResult> { ReportComparer.CheckProbe("a", 1f, 1f, 0f) };

        Assert.That(ReportComparer.AllPass(checks, new List<string> { "SetTerrain: boom" }), Is.False);
    }

    [Test]
    public void AllPass_PassingChecksNoErrors_IsTrue()
    {
        var checks = new List<ProbeCheckResult> { ReportComparer.CheckProbe("a", 1f, 1f, 0f) };

        Assert.That(ReportComparer.AllPass(checks, new List<string>()), Is.True);
    }

    [Test]
    public void AllPass_NoChecksNoErrors_IsTrue()
    {
        Assert.That(ReportComparer.AllPass(new List<ProbeCheckResult>(), new List<string>()), Is.True);
    }
}
