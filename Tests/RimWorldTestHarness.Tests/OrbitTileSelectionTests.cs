using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// The selection rule behind LandInOrbit. Worth real coverage rather than a smoke test, because
// getting it subtly wrong is invisible at run time: the step would still generate an orbital map and
// still report success, just at a latitude nobody asked for — and with stationary orbits that is a
// different sun path, so every probe pinned against it would be pinned against the wrong world.
[TestFixture]
public class OrbitTileSelectionTests
{
    // Great-circle separation, sanity-checked against distances anyone can verify by hand.
    [TestCase(0f, 0f, 0f, 0f, 0f, TestName = "Separation_SamePointIsZero")]
    [TestCase(0f, 0f, 0f, 90f, 90f, TestName = "Separation_QuarterTurnAlongTheEquator")]
    [TestCase(0f, 0f, 90f, 0f, 90f, TestName = "Separation_EquatorToPole")]
    [TestCase(-90f, 0f, 90f, 0f, 180f, TestName = "Separation_PoleToPole")]
    [TestCase(45f, 10f, 45f, 10f, 0f, TestName = "Separation_SameMidLatitudePoint")]
    public void Separation_MatchesKnownAngles(
        double latA, double lonA, double latB, double lonB, double expected)
    {
        Assert.That(OrbitTileSelection.SeparationDegrees(latA, lonA, latB, lonB),
            Is.EqualTo(expected).Within(1e-6));
    }

    // Longitude is irrelevant at a pole, and the naive formula's dot product goes a hair over 1 there.
    // Acos of anything above 1 is NaN, and a NaN offset compares false against every candidate — so
    // PickNearest would return "no tile at all" for a request nothing is wrong with.
    [Test]
    public void Separation_AtThePolesDoesNotGoNaN()
    {
        Assert.That(OrbitTileSelection.SeparationDegrees(90, 0, 90, 180), Is.EqualTo(0).Within(1e-6));
    }

    [Test]
    public void PickNearest_WithNoTilesReturnsMinusOne()
    {
        int index = OrbitTileSelection.PickNearest(
            new float[0], new float[0], 45f, null, out double offset);

        Assert.Multiple(() =>
        {
            Assert.That(index, Is.EqualTo(-1));
            Assert.That(double.IsPositiveInfinity(offset), Is.True,
                "an offset of 0 would read as a perfect match on a layer with no tiles at all");
        });
    }

    // Latitude-only is the common case: longitude changes the local-time offset, not the sun's
    // elevation, so a scenario naming just a latitude should take whichever tile sits closest to that
    // band no matter where round the world it is.
    [Test]
    public void PickNearest_WithoutALongitudeIgnoresIt()
    {
        float[] lats = { 0f, 44f, 80f };
        float[] lons = { 0f, 170f, 0f };

        int index = OrbitTileSelection.PickNearest(lats, lons, 45f, null, out double offset);

        Assert.Multiple(() =>
        {
            Assert.That(index, Is.EqualTo(1), "the 44° tile is nearest in latitude despite its longitude");
            Assert.That(offset, Is.EqualTo(1.0).Within(1e-6));
        });
    }

    // ...and naming one has to actually constrain the answer, or the arg would be decorative.
    [Test]
    public void PickNearest_WithALongitudePrefersTheNearerMeridian()
    {
        float[] lats = { 44f, 46f };
        float[] lons = { 170f, 0f };

        int index = OrbitTileSelection.PickNearest(lats, lons, 45f, 0f, out double offset);

        Assert.Multiple(() =>
        {
            Assert.That(index, Is.EqualTo(1), "the 44° tile is nearer in latitude but 170° away in longitude");
            Assert.That(offset, Is.EqualTo(1.0).Within(1e-6));
        });
    }

    // Determinism is the whole point of pinning a tile: a probe expectation recorded against tile N is
    // only an expectation if the next run over the same world picks tile N again.
    [Test]
    public void PickNearest_BreaksTiesTowardTheLowestIndex()
    {
        float[] lats = { 45f, 45f, 45f };
        float[] lons = { 10f, 20f, 30f };

        int index = OrbitTileSelection.PickNearest(lats, lons, 45f, null, out _);

        Assert.That(index, Is.EqualTo(0));
    }

    // The offset is what the step gates on, so it has to be the real angular distance and not the
    // latitude delta that happens to be equal to it near the equator.
    [Test]
    public void PickNearest_ReportsTheAngularOffsetNotTheLatitudeDelta()
    {
        float[] lats = { 60f };
        float[] lons = { 30f };

        OrbitTileSelection.PickNearest(lats, lons, 60f, 0f, out double offset);

        // cos(d) = sin²60 + cos²60·cos30 = 0.9665 => d ≈ 14.87°. Not 30 (the longitude delta) and not
        // 0 (the latitude delta), which are the two wrong answers a mis-implementation lands on.
        Assert.That(offset, Is.EqualTo(14.8709).Within(0.01));
    }
}
