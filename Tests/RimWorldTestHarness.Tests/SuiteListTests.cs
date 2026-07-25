using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

[TestFixture]
public class SuiteListTests
{
    private static List<string> Parse(string text, out List<string> errors, string baseDir = "/suites")
    {
        errors = new List<string>();
        return SuiteList.Parse(text, baseDir, errors);
    }

    [Test]
    public void Parse_KeepsOrderAndResolvesRelativePaths()
    {
        var paths = Parse("a.json\nsub/b.json\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(paths, Is.EqualTo(new[] { "/suites/a.json", "/suites/sub/b.json" }));
        });
    }

    [Test]
    public void Parse_LeavesAbsolutePathsAlone()
    {
        var paths = Parse("/elsewhere/a.json\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(paths, Is.EqualTo(new[] { "/elsewhere/a.json" }));
        });
    }

    [TestCase("a.json\n\n\nb.json\n", TestName = "Parse_SkipsBlankLines")]
    [TestCase("# a comment\na.json\n  # indented comment\nb.json\n", TestName = "Parse_SkipsWholeLineComments")]
    [TestCase("  a.json  \n\tb.json\t\n", TestName = "Parse_TrimsSurroundingWhitespace")]
    public void Parse_IgnoresNoise(string text)
    {
        var paths = Parse(text, out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(paths, Is.EqualTo(new[] { "/suites/a.json", "/suites/b.json" }));
        });
    }

    // '#' is legal in a filename, so truncating at one would silently turn a real path into a
    // different (missing) one.
    [Test]
    public void Parse_DoesNotTreatTrailingHashAsComment()
    {
        var paths = Parse("odd#name.json\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(paths, Is.EqualTo(new[] { "/suites/odd#name.json" }));
        });
    }

    [Test]
    public void Parse_HandlesCrlfLineEndings()
    {
        var paths = Parse("a.json\r\nb.json\r\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.Empty);
            Assert.That(paths, Is.EqualTo(new[] { "/suites/a.json", "/suites/b.json" }));
        });
    }

    // A repeated entry would run one scenario twice under one name, producing two indistinguishable
    // reports and two sets of screenshots sharing a filename prefix.
    [Test]
    public void Parse_RejectsDuplicateEntries()
    {
        var paths = Parse("a.json\nb.json\na.json\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.EqualTo(new[] { "/suites/a.json", "/suites/b.json" }));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("line 3").And.Contain("more than once"));
        });
    }

    [Test]
    public void Parse_DetectsDuplicatesThroughDifferentSpellings()
    {
        var paths = Parse("a.json\n/suites/a.json\n", out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(paths, Has.Count.EqualTo(1));
            Assert.That(errors, Has.Count.EqualTo(1));
        });
    }

    // The vacuous-truth guard: an empty suite must be an error, not an empty success.
    [TestCase("")]
    [TestCase("\n\n")]
    [TestCase("# only comments\n")]
    public void Parse_RejectsEmptySuite(string text)
    {
        var paths = Parse(text, out var errors);

        Assert.Multiple(() =>
        {
            Assert.That(paths, Is.Empty);
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("empty"));
        });
    }
}
