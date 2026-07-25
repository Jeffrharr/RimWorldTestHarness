using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Regression coverage for the clock math behind SetTime/SetSeason. The bug this locks down: on a
// fresh colony (which starts a few ticks in, at ~6am), requesting an EARLIER time-of-day computes a
// NEGATIVE ticksGame — and a game has no moment before its own start, so the engine silently rejects
// it and the clock never moves. LocalTimeToTicksGame must roll such requests forward whole years,
// keeping day-of-year and hour-of-day exact. Constants are RimWorld 1.6's GenDate values (pinned by
// the API-compat suite).
[TestFixture]
public class GameClockTests
{
    private const int TicksPerHour = 2500;
    private const int TicksPerDay = 60000;    // 24 * 2500
    private const int TicksPerYear = 3600000; // 60 * 60000

    // Model a colony that began at abs tick `gameStartAbs` (i.e. some day/hour) and has run for
    // `elapsed` ticks. That's exactly the (currentAbs, currentTicksGame) pair the game exposes, and
    // gameStartAbsTick = currentAbs - currentTicksGame falls back out of it. Longitude offset 0.
    private sealed record Jump(long Result, long CurrentAbs, long CurrentTicksGame);

    private static Jump Compute(int dayOfYear, float hour, long gameStartAbs, long elapsed)
    {
        long currentAbs = gameStartAbs + elapsed;
        long result = GameClock.LocalTimeToTicksGame(currentAbs, elapsed, dayOfYear, hour, 0,
            TicksPerHour, TicksPerDay, TicksPerYear);
        return new Jump(result, currentAbs, elapsed);
    }

    [Test]
    public void ForwardJumpSameDay_MovesToRequestedHour()
    {
        // Fresh colony: started day 0 06:00, 60 ticks in. Ask for 15:00 (forward) — matches the live
        // smoke test, which observed ticksGame 22500.
        Jump j = Compute(0, 15f, gameStartAbs: 6 * TicksPerHour, elapsed: 60);
        Assert.That(j.Result, Is.EqualTo(22500));
        Assert.That(HourOfDay(j), Is.EqualTo(15f).Within(0.01f));
    }

    [Test]
    public void BackwardJumpSameDay_NeverGoesNegative()
    {
        // The reported bug: fresh colony at day 0 06:00, ask for 03:00. Naive math yields -7500.
        Jump j = Compute(0, 3f, gameStartAbs: 6 * TicksPerHour, elapsed: 60);

        Assert.That(j.Result, Is.GreaterThanOrEqualTo(0), "ticksGame must never be negative");
        Assert.That(DayOfYear(j), Is.EqualTo(0), "day-of-year preserved");
        Assert.That(HourOfDay(j), Is.EqualTo(3f).Within(0.01f), "hour-of-day is the requested 3am");
    }

    [Test]
    public void BackwardJump_LaterDay_PreservesDayAndHour()
    {
        // Fresh colony at day 10 06:00; ask for day 10 03:00 (backward within the same day).
        Jump j = Compute(10, 3f, gameStartAbs: 10L * TicksPerDay + 6 * TicksPerHour, elapsed: 60);

        Assert.That(j.Result, Is.GreaterThanOrEqualTo(0));
        Assert.That(DayOfYear(j), Is.EqualTo(10));
        Assert.That(HourOfDay(j), Is.EqualTo(3f).Within(0.01f));
    }

    [Test]
    public void BackwardJump_OnAgedColony_DoesNotRollAYear()
    {
        // A colony running well past its start has headroom: a backward request that still lands after
        // game start must NOT roll a year — it's a plain in-run jump.
        long gameStartAbs = 100L * TicksPerYear;           // began 100 years into the abs timeline
        long elapsed = 5L * TicksPerDay + 6 * TicksPerHour; // now day 5, 06:00 of this run
        Jump j = Compute(5, 3f, gameStartAbs, elapsed);

        Assert.That(j.Result, Is.EqualTo(5L * TicksPerDay + 3 * TicksPerHour));
        Assert.That(HourOfDay(j), Is.EqualTo(3f).Within(0.01f));
    }

    [Test]
    public void HourIsClampedIntoValidRange()
    {
        // hour 24.0 must not spill into the next day (clamped to the last tick of the day).
        Jump j = Compute(0, 24f, gameStartAbs: 0, elapsed: 0);
        Assert.That(j.Result % TicksPerDay, Is.EqualTo(TicksPerDay - 1));
    }

    // --- helpers mirroring GenDate's own derivation (gameStartAbsTick = currentAbs - currentTicksGame) ---

    private static int DayOfYear(Jump j)
    {
        long abs = j.Result + (j.CurrentAbs - j.CurrentTicksGame);
        return (int)(PositiveMod(abs, TicksPerYear) / TicksPerDay);
    }

    private static float HourOfDay(Jump j)
    {
        long abs = j.Result + (j.CurrentAbs - j.CurrentTicksGame);
        return (float)PositiveMod(abs, TicksPerDay) / TicksPerHour;
    }

    private static long PositiveMod(long value, long modulus) => ((value % modulus) + modulus) % modulus;
}
