using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Covers the arithmetic that turns Dubs Performance Analyzer's raw per-frame samples into a report
// table. Every one of these is a division whose wrong answer is plausible rather than obviously
// broken — a mean over the frames we ASKED for instead of the frames recorded, a percentage of an
// unrecorded denominator, microseconds-per-call over zero calls — which is why the whole thing lives
// in Shared with no game attached.
[TestFixture]
public class ProfileMathTests
{
    private static ProfileSample Sample(
        string label, double avgMs, double maxMs, double totalMs, double calls, double maxCalls = 0) =>
        new ProfileSample
        {
            Label = label,
            AverageMs = avgMs,
            MaxMs = maxMs,
            TotalMs = totalMs,
            Calls = calls,
            MaxCallsPerFrame = maxCalls,
        };

    private static ProfileTable Build(params ProfileSample[] samples) =>
        ProfileMath.Build("t", "harmony", "Mod", 600, 600, 2.847, samples);

    // The scenario from issue #23: 0.453 ms/frame average over 3,738 calls in 600 frames. The numbers
    // a probe cannot produce are the last two — calls per frame, and the worst frame being 6.6x the
    // mean.
    [Test]
    public void DerivesPerFrameAndPerCallFiguresFromOneSample()
    {
        ProfileTable table = Build(Sample("Mod.Patch_A:Postfix", 0.453, 2.999, 271.8, 3738, 12));

        ProfileRow row = table.Rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.AvgMsPerFrame, Is.EqualTo(0.453).Within(1e-9));
            Assert.That(row.MaxMsPerFrame, Is.EqualTo(2.999).Within(1e-9));
            Assert.That(row.Calls, Is.EqualTo(3738));
            Assert.That(row.MaxCallsPerFrame, Is.EqualTo(12));
            Assert.That(row.CallsPerFrame, Is.EqualTo(6.23).Within(1e-9));
            // 271.8 ms over 3,738 calls = 72.71 µs each.
            Assert.That(row.AvgUsPerCall, Is.EqualTo(72.71).Within(0.01));
        });
    }

    // The percentage trap the issue calls out: 15.9% of a 2.847 ms frame is only ~2.7% of a 60 fps
    // budget, and a report that prints only the first number reads six times more alarming than it is.
    [Test]
    public void RecordsBothTheAchievedFramePercentageAndTheSixtyFpsOne()
    {
        ProfileTable table = Build(Sample("Mod.Patch_A:Postfix", 0.453, 2.999, 271.8, 3738));

        ProfileRow row = table.Rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.PercentOfFrame, Is.EqualTo(15.911).Within(0.001));
            Assert.That(row.PercentOfSixtyFpsBudget, Is.EqualTo(2.718).Within(0.001));
            // The denominator travels with the percentage, which is the actual fix for the trap.
            Assert.That(table.FrameMs, Is.EqualTo(2.847).Within(1e-9));
            Assert.That(table.FramesPerSecond, Is.EqualTo(351.25).Within(0.01));
        });
    }

    [Test]
    public void SumsFilteredRowsIntoASubsystemTotal()
    {
        ProfileTable table = Build(
            Sample("Mod.Patch_A:Postfix", 0.453, 2.999, 271.8, 3738),
            Sample("Mod.Patch_B:Prefix", 0.032, 0.302, 19.2, 3734),
            Sample("Mod.Patch_C:Postfix", 0.017, 0.088, 10.2, 7432));

        Assert.Multiple(() =>
        {
            Assert.That(table.TotalAvgMsPerFrame, Is.EqualTo(0.502).Within(1e-9));
            Assert.That(table.TotalPercentOfFrame, Is.EqualTo(17.633).Within(0.001));
            Assert.That(table.TotalPercentOfSixtyFpsBudget, Is.EqualTo(3.012).Within(0.001));
        });
    }

    // Rows come out of a ConcurrentDictionary in whatever order it enumerated. Sorting is what makes
    // two runs of the same scenario produce a diffable report rather than a reshuffled one.
    [Test]
    public void SortsRowsByCostDescendingWithAStableTieBreak()
    {
        ProfileTable table = Build(
            Sample("Mod.B:M", 0.1, 1, 60, 100),
            Sample("Mod.A:M", 0.1, 1, 60, 100),
            Sample("Mod.C:M", 0.9, 1, 540, 100));

        Assert.That(table.Rows.Select(r => r.Label),
                    Is.EqualTo(new[] { "Mod.C:M", "Mod.A:M", "Mod.B:M" }));
    }

    [Test]
    public void FiltersToThePrefixAndRecordsHowManyRowsThereWereBefore()
    {
        ProfileTable table = ProfileMath.Build(
            "t", "harmony", "Mod", 600, 600, 2.0,
            new[]
            {
                Sample("Mod.A:M", 0.1, 1, 60, 100),
                Sample("Verse.Thing:Tick", 5.0, 9, 3000, 90000),
                Sample("OtherMod.A:M", 0.2, 1, 120, 100),
            });

        Assert.Multiple(() =>
        {
            Assert.That(table.Rows.Select(r => r.Label), Is.EqualTo(new[] { "Mod.A:M" }));
            Assert.That(table.RowsBeforeFilter, Is.EqualTo(3));
        });
    }

    // Anchored at the start and case-sensitive: the analyzer's label is "Namespace.Type:Method", so a
    // mod's root namespace is exactly the prefix an author writes, and a case-insensitive match would
    // let a misspelling work here and nowhere else.
    [TestCase("Mod.A:M", "Mod", ExpectedResult = true)]
    [TestCase("Mod.A:M", "Mod.A", ExpectedResult = true)]
    [TestCase("Mod.A:M", "mod", ExpectedResult = false)]
    [TestCase("Mod.A:M", "A:M", ExpectedResult = false)]
    [TestCase("Mod.A:M", "", ExpectedResult = true)]
    public bool MatchesPrefixIsAnchoredAndOrdinal(string label, string prefix) =>
        ProfileMath.MatchesPrefix(label, prefix);

    // A NaN or Infinity here would travel into the JSON report and break System.Text.Json's writer,
    // taking the whole run's report with it over a row that merely never ran.
    [Test]
    public void ZeroCallsAndZeroFramesProduceZerosRatherThanNaN()
    {
        ProfileTable table = ProfileMath.Build("t", "harmony", "Mod", 600, 0, 0, new[]
        {
            Sample("Mod.A:M", 0, 0, 0, 0),
        });

        ProfileRow row = table.Rows.Single();
        Assert.Multiple(() =>
        {
            Assert.That(row.AvgUsPerCall, Is.Zero);
            Assert.That(row.CallsPerFrame, Is.Zero);
            Assert.That(row.PercentOfFrame, Is.Zero);
            Assert.That(table.FramesPerSecond, Is.Zero);
        });
    }

    // The means divide by the frames the analyzer actually RECORDED, which a long event or the ring
    // buffer can make smaller than the frames the scenario asked for. Both are in the table so a
    // reader comparing two runs can see the divisor moved.
    [Test]
    public void RecordsRequestedAndMeasuredFramesSeparately()
    {
        ProfileTable table = ProfileMath.Build("t", "harmony", "Mod", 600, 480, 2.0, new[]
        {
            Sample("Mod.A:M", 0.5, 1, 240, 960),
        });

        Assert.Multiple(() =>
        {
            Assert.That(table.RequestedFrames, Is.EqualTo(600));
            Assert.That(table.MeasuredFrames, Is.EqualTo(480));
            Assert.That(table.Rows.Single().CallsPerFrame, Is.EqualTo(2).Within(1e-9));
        });
    }

    // The caveats ride in the report because that is where someone is standing when they misread these
    // numbers. A note in a README they last opened a month ago is not in the room.
    [Test]
    public void EveryTableCarriesTheCallCountAndPercentageCaveats()
    {
        ProfileTable table = Build(Sample("Mod.A:M", 0.1, 1, 60, 100));

        Assert.Multiple(() =>
        {
            Assert.That(table.Notes, Contains.Item(ProfileMath.CallCountNote));
            Assert.That(table.Notes, Contains.Item(ProfileMath.PercentNote));
            Assert.That(table.Notes, Contains.Item(ProfileMath.ProfilerOverheadNote));
        });
    }

    [Test]
    public void NoSamplesAtAllIsAnEmptyTableRatherThanAThrow()
    {
        ProfileTable table = ProfileMath.Build("t", "harmony", "Mod", 600, 600, 2.0, null!);

        Assert.Multiple(() =>
        {
            Assert.That(table.Rows, Is.Empty);
            Assert.That(table.RowsBeforeFilter, Is.Zero);
            Assert.That(table.TotalAvgMsPerFrame, Is.Zero);
        });
    }
}

// Covers reading one number back out of a table, which is the half of ProfileAssert that can silently
// assert against the wrong thing.
[TestFixture]
public class ProfileMetricsTests
{
    private static ProfileTable Table() => ProfileMath.Build(
        "aurora", "harmony", "Mod", 600, 600, 2.0,
        new[]
        {
            new ProfileSample { Label = "Mod.Patch_Curtain:Postfix", AverageMs = 0.4, MaxMs = 3.0, TotalMs = 240, Calls = 3600, MaxCallsPerFrame = 9 },
            new ProfileSample { Label = "Mod.Patch_Sky:Prefix", AverageMs = 0.1, MaxMs = 0.5, TotalMs = 60, Calls = 1200, MaxCallsPerFrame = 4 },
        });

    [TestCase(ProfileMetrics.AvgMsPerFrame, ExpectedResult = 0.4)]
    [TestCase(ProfileMetrics.MaxMsPerFrame, ExpectedResult = 3.0)]
    [TestCase(ProfileMetrics.Calls, ExpectedResult = 3600)]
    [TestCase(ProfileMetrics.CallsPerFrame, ExpectedResult = 6.0)]
    [TestCase(ProfileMetrics.MaxCallsPerFrame, ExpectedResult = 9)]
    public double ReadsEachMetricOffTheNamedRow(string metric)
    {
        Assert.That(ProfileMetrics.TryRead(Table(), "Mod.Patch_Curtain:Postfix", metric, out double v, out _),
                    Is.True);
        return v;
    }

    // Summing the rows' worst frames would be nonsense — those were probably different frames — so the
    // subsystem's max is the worst any one row had.
    [Test]
    public void TotalsSumTimeAndCallsButTakeTheMaximumOfMaxima()
    {
        ProfileTable table = Table();

        Assert.Multiple(() =>
        {
            Assert.That(Read(table, ProfileMetrics.AvgMsPerFrame), Is.EqualTo(0.5).Within(1e-9));
            Assert.That(Read(table, ProfileMetrics.MaxMsPerFrame), Is.EqualTo(3.0).Within(1e-9));
            Assert.That(Read(table, ProfileMetrics.Calls), Is.EqualTo(4800).Within(1e-9));
            Assert.That(Read(table, ProfileMetrics.MaxCallsPerFrame), Is.EqualTo(9).Within(1e-9));
        });
    }

    // Call-weighted, not the mean of the rows' means: a method called twice must not weigh the same as
    // one called ten thousand times.
    [Test]
    public void TotalPerCallCostIsWeightedByCallCount()
    {
        // 300 ms over 4,800 calls = 62.5 µs, not (66.67 + 50) / 2.
        Assert.That(Read(Table(), ProfileMetrics.AvgUsPerCall), Is.EqualTo(62.5).Within(0.01));
    }

    private static double Read(ProfileTable table, string metric)
    {
        Assert.That(ProfileMetrics.TryRead(table, ProfileMetrics.TotalsLabel, metric, out double v, out _),
                    Is.True);
        return v;
    }

    // Long labels break on any rename of a private patch class, so a unique fragment is allowed.
    [Test]
    public void AUniqueSubstringResolvesToItsRow()
    {
        Assert.That(ProfileMetrics.TryRead(Table(), "Patch_Sky", ProfileMetrics.Calls, out double v, out _),
                    Is.True);
        Assert.That(v, Is.EqualTo(1200));
    }

    // First-match-wins here would assert against whichever row sorted higher, which is precisely when
    // a wrong pass looks like a right one.
    [Test]
    public void AnAmbiguousSubstringIsAnErrorRatherThanAGuess()
    {
        bool ok = ProfileMetrics.TryRead(Table(), "Mod.Patch", ProfileMetrics.Calls, out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("matches 2 rows"));
        });
    }

    // A row that vanished (renamed patch, changed prefix) must fail loudly. Reading 0 would pass any
    // "at most" bound it was given — a green run that verified nothing.
    [Test]
    public void AMissingRowIsAnErrorRatherThanZero()
    {
        bool ok = ProfileMetrics.TryRead(Table(), "Mod.Gone:M", ProfileMetrics.Calls, out double v, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(v, Is.Zero);
            Assert.That(error, Does.Contain("no profiled method matching"));
        });
    }

    [Test]
    public void AnUnknownMetricNameIsAnError()
    {
        bool ok = ProfileMetrics.TryRead(Table(), ProfileMetrics.TotalsLabel, "msPerCall", out _, out string? error);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(error, Does.Contain("unknown profile metric"));
        });
    }
}
