using RimWorld;
using Verse;

namespace RimWorldTestHarness.Mod.Probes.BuiltIn;

// The harness's own clock, readable by a scenario. Every other probe in the system belongs to a mod
// under test; these belong here, because what they measure is the harness's own time manipulation
// rather than anyone's rendering.
//
// They exist because a whole class of scenario failure is invisible without them. SetSeason and
// SetTime jump to a day-of-year and an hour *within whatever year the clock is already in* — see
// GameClock.LocalTimeToTicksGame, where yearStart is derived from currentAbs. Anything keyed on the
// ABSOLUTE tick rather than on the time of day (moon phase, most obviously) therefore reads
// differently depending on what ran before, while every step reports success. Two CelestialLighting
// scenarios were failing in-suite and passing standalone for exactly this reason, and the first
// question — "is the clock actually where the scenario thinks it is?" — had no instrument to answer.
//
// Reported as float because IProbe is a float interface. TicksAbs is well inside float's exact
// integer range for any plausible run (2^24 ticks is ~466 in-game years), so the conversion is not
// lossy in practice; ticks_abs_day exists anyway for the case where a reader wants the coarse number
// without thinking about it.
public sealed class TicksAbsProbe : IProbe, IProbeMetadata
{
    public string Name => "ticks_abs";

    public string? Description =>
        "Absolute game tick (GenDate.TicksAbs). The quantity moon phase and other " +
        "calendar-keyed effects are a function of. Pin or anchor it when a scenario's " +
        "expectations depend on more than the time of day.";

    public string? Unit => "ticks";

    public float Read(Map map) => Find.TickManager.TicksAbs;
}

// The same clock in the unit a human reasons in. A scenario that drifted by a few days shows an
// obvious integer difference here, where the raw tick count differs by an unmemorable six-figure
// number and reads as noise.
public sealed class AbsoluteDayProbe : IProbe, IProbeMetadata
{
    public string Name => "ticks_abs_day";

    public string? Description =>
        "Absolute day index (TicksAbs / TicksPerDay), ignoring longitude. The readable form " +
        "of ticks_abs: a scenario that inherited the wrong year differs here by whole days.";

    public string? Unit => "days";

    public float Read(Map map) => Find.TickManager.TicksAbs / GenDate.TicksPerDay;
}
