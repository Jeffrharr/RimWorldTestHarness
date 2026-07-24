using System;

namespace RimWorldTestHarness.Shared;

// Pure clock arithmetic for the SetTime/SetSeason steps, pulled out of StepExecutor (which touches
// live Verse types) so the tricky part — turning a requested day-of-year + hour into the ticksGame
// value the game must be set to — is dependency-free and unit-testable offline. The caller feeds in
// GenDate's real constants and longitude offset so nothing here hardcodes game numbers.
public static class GameClock
{
    // Returns the ticksGame value that makes GenDate.DayOfYear/HourFloat read back exactly
    // (dayOfYear, hour) at the given longitude. Mirrors GenDate's own DayOfYear/HourOfDay derivation
    // (a PositiveModRemap over TicksAbs plus a longitude offset) in reverse.
    //
    // Crucial correctness point: ticksGame can never be negative — a game has no moment before its
    // own start (gameStartAbsTick). On a fresh colony the current in-year day/hour is usually only
    // minutes past game start, so any *earlier* requested time (e.g. "set to 3am" when it's 6am on
    // day 0) would compute a negative tick, which the engine silently rejects and the clock doesn't
    // move. We roll forward whole years until the tick is non-negative: adding TicksPerYear shifts by
    // exactly DaysPerYear days, so the requested day-of-year AND hour-of-day are preserved exactly
    // (season, sun angle and lighting are identical — only the year label differs).
    public static long LocalTimeToTicksGame(
        long currentAbs,
        long currentTicksGame,
        int dayOfYear,
        float hour,
        long longitudeOffset,
        int ticksPerHour,
        int ticksPerDay,
        int ticksPerYear)
    {
        long currentLocal = currentAbs + longitudeOffset;
        long yearStart = currentLocal - PositiveMod(currentLocal, ticksPerYear);

        int dayTick = Clamp((int)Math.Round(hour * ticksPerHour), 0, ticksPerDay - 1);
        long targetLocal = yearStart + (long)dayOfYear * ticksPerDay + dayTick;
        long targetAbs = targetLocal - longitudeOffset;

        long gameStartAbsTick = currentAbs - currentTicksGame;
        long newTicksGame = targetAbs - gameStartAbsTick;

        while (newTicksGame < 0)
            newTicksGame += ticksPerYear;

        return newTicksGame;
    }

    private static long PositiveMod(long value, long modulus) => ((value % modulus) + modulus) % modulus;

    private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);
}
