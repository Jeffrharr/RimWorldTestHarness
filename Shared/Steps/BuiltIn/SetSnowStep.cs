using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// SetSnow — lay (or clear) snow over a rectangle, directly.
//
// WHY A SCENARIO NEEDS THIS RATHER THAN JUST WEATHER. Snow on the ground is not a property of the
// weather; it is an accumulation. `SetWeather SnowHard` only starts flakes falling, and depth then
// grows tick by tick as a function of the map's TEMPERATURE — so on a warm tile it never arrives at
// all. A scenario trying to photograph a snowy scene therefore has no working lever: it can set the
// weather (draws flakes, ground stays bare), fast-forward (costs minutes of real time and still
// gives nothing above freezing), or swap the biome to something arctic (heavy-handed, dirties the
// map beyond repair, and STILL needs the ticks). All three were tried against CelestialLighting's
// showcase scenario before this step existed, and all three came back with bare ground.
//
// Snow is also the one visual state that changes what the lighting mods under test are lit against
// — a white ground is the highest-albedo surface in the game — so "cannot film snow" was a real gap
// rather than a cosmetic one.
//
// Depth is what vanilla calls it: 0 is clear, 1 is the deepest snow the game renders
// (SnowGrid.MaxDepth). Values in between give the partial dustings that read as early snowfall.
//
// This file plus Mod/Steps/BuiltIn/SetSnowAction.cs is the entire step — no switch arm, no
// registration call, per CONTRIBUTING.md's "Adding a step".
public sealed class SetSnowStep : IStepSpec
{
    public const string StepType = "SetSnow";
    public const string DepthArg = "depth";     // 0..1, default 1
    public const string WidthArg = "width";
    public const string HeightArg = "height";

    // Matches SetTerrain's 40x40 rather than SetRoof's 7x7: snow is ground cover like terrain, and a
    // scenario asking for snow wants the backdrop covered, not a patch in the middle of it.
    public const int DefaultWidth = 40;
    public const int DefaultHeight = 40;
    public const double DefaultDepth = 1;

    public string Type => StepType;

    // Residue.Map, like SetTerrain and SetRoof. Snow outlives the scenario that laid it and changes
    // what every later frame is lit against, so a suite must reload rather than soft-reset past it.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Not live-callable, matching the scene steps: the companion channel points at a real colony,
    // and burying someone's base in snow over a file-drop channel is exactly the kind of surprise
    // that channel promises not to spring.
    public bool LiveCallable => false;

    // Validation IS planning, as with the scene steps: ArgReader is internal to this assembly, so
    // the Mod-side adapter cannot re-parse args itself and instead receives a finished plan. One
    // parse, one set of rules, no chance of the two halves disagreeing about a default.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    public static bool TryPlan(
        IReadOnlyDictionary<string, string> args, out SnowPlan plan, out string? error)
    {
        plan = new SnowPlan();

        string[] known = { StepArgs.SceneAnchor, StepArgs.SceneOffset, DepthArg, WidthArg, HeightArg };
        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;

        if (!ArgReader.TryReadDouble(args, DepthArg, DefaultDepth, out double depth, out error))
            return false;

        // Clamping instead would hide a typo: 10 almost certainly means someone thought depth was a
        // percentage or a cell count, and silently laying MaxDepth would look like it worked.
        if (depth < 0 || depth > 1)
        {
            error = $"'{DepthArg}' must be between 0 and 1 (got {ArgReader.Format(depth)}); " +
                    "0 clears snow, 1 is the deepest the game renders";
            return false;
        }

        plan.Depth = depth;

        if (!ReadPositive(args, WidthArg, DefaultWidth, out plan.Width, out error))
            return false;
        if (!ReadPositive(args, HeightArg, DefaultHeight, out plan.Height, out error))
            return false;

        // Same long-multiply guard as SetTerrain, for the same reason: two large-but-legal ints
        // overflow an int product into a small positive number and sail past the cap.
        if ((long)plan.Width * plan.Height > SceneLayout.MaxTerrainCells)
        {
            error = $"{plan.Width}x{plan.Height} exceeds the {SceneLayout.MaxTerrainCells}-cell snow cap";
            return false;
        }

        // The anchor is parsed by the same reader the scene steps use, so "center" and "125,125"
        // mean here exactly what they mean there.
        return SceneLayout.ReadAnchor(args, plan, out error);
    }

    private static bool ReadPositive(
        IReadOnlyDictionary<string, string> args, string key, int fallback, out int value, out string? error)
    {
        if (!ArgReader.TryReadInt(args, key, fallback, out value, out error))
            return false;

        if (value < 1)
        {
            error = $"'{key}' must be at least 1 (got {value})";
            return false;
        }

        error = null;
        return true;
    }
}

// Where and how deep, as plain numbers. Lives here beside its step rather than in SceneLayout with
// the other three plans, because it is the only one that isn't geometry the scene builder places —
// it carries a depth, and nothing in SceneLayout's vocabulary needs to know about it.
//
// Implements IAnchoredPlan so SceneLayout.ReadAnchor fills the anchor fields, which is what makes
// "center" and "125,125" mean the same thing here as in PlaceThings.
public sealed class SnowPlan : IAnchoredPlan
{
    // 0 clears, 1 is SnowGrid.MaxDepth. Validated to that range by TryPlan.
    public double Depth = SetSnowStep.DefaultDepth;
    public int Width;
    public int Height;

    public SceneAnchorKind Anchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorZ { get; set; }
    public int OffsetX { get; set; }
    public int OffsetZ { get; set; }
}
