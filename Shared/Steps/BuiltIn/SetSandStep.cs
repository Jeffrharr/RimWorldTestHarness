using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// SetSand — lay (or clear) sand over a rectangle, directly. Odyssey's desert sibling of SetSnow; see
// SetSnowStep.cs for the full rationale (weather starting a sandstorm doesn't move the grid either,
// same three dead ends apply). This file exists rather than SetSnow alone because the two grids are
// separate accumulations on separate terrain (snowable vs. sand-holding), so a scenario photographing
// a desert map needs its own lever.
//
// Depth is what vanilla calls it: 0 is clear, 1 is the deepest sand the game renders
// (SandGrid.MaxDepth). Values in between give partial dune cover.
//
// This file plus Mod/Steps/BuiltIn/SetSandAction.cs is the entire step — no switch arm, no
// registration call, per CONTRIBUTING.md's "Adding a step".
public sealed class SetSandStep : IStepSpec
{
    public const string StepType = "SetSand";
    public const string DepthArg = "depth";     // 0..1, default 1
    public const string WidthArg = "width";
    public const string HeightArg = "height";

    // Matches SetSnow's 40x40 rather than SetRoof's 7x7: sand is ground cover like terrain, and a
    // scenario asking for sand wants the backdrop covered, not a patch in the middle of it.
    public const int DefaultWidth = 40;
    public const int DefaultHeight = 40;
    public const double DefaultDepth = 1;

    public string Type => StepType;

    // Residue.Map, like SetSnow, SetTerrain and SetRoof. Sand outlives the scenario that laid it and
    // changes every later frame it's lit against, so a suite reloads rather than soft-resets past it.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Not live-callable, matching the other scene steps: the companion channel points at a real
    // colony, and burying someone's base in sand over a file-drop channel is exactly the kind of
    // surprise that channel promises not to spring.
    public bool LiveCallable => false;

    // Validation IS the planning, like the other scene steps: ArgReader is internal to this assembly,
    // so the Mod-side adapter cannot re-parse the args itself and instead receives the finished plan.
    // One parse, one set of rules, no chance of the two halves disagreeing about a default.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    public static bool TryPlan(
        IReadOnlyDictionary<string, string> args, out SandPlan plan, out string? error)
    {
        plan = new SandPlan();

        string[] known = { StepArgs.SceneAnchor, StepArgs.SceneOffset, DepthArg, WidthArg, HeightArg };
        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;

        if (!ArgReader.TryReadDouble(args, DepthArg, DefaultDepth, out double depth, out error))
            return false;

        // Clamping instead would hide a typo: 10 almost certainly means someone thought depth was a
        // percentage or a cell count, and silently laying MaxDepth would look like it worked.
        if (depth < 0 || depth > 1)
        {
            error = $"'{DepthArg}' must be between 0 and 1 (got {ArgReader.Format(depth)})";
            return false;
        }
        plan.Depth = depth;

        if (!ReadPositive(args, WidthArg, DefaultWidth, out plan.Width, out error))
            return false;
        if (!ReadPositive(args, HeightArg, DefaultHeight, out plan.Height, out error))
            return false;

        // Same long-multiply guard as SetSnow, same reason: two large-but-legal ints can overflow an
        // int product into a small positive number and sail past the cap.
        if ((long)plan.Width * plan.Height > SceneLayout.MaxTerrainCells)
        {
            error = $"{plan.Width}x{plan.Height} exceeds the {SceneLayout.MaxTerrainCells}-cell sand cap";
            return false;
        }

        // Anchor is parsed by the same reader the scene steps use, so "center" and "125,125" mean
        // here exactly what they mean there.
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
// the other three plans, because it's the only one that isn't geometry the scene builder places —
// it carries a depth, and nothing in SceneLayout's vocabulary needs to know about it.
//
// Implements IAnchoredPlan so SceneLayout.ReadAnchor fills in the anchor fields, which makes
// "center" and "125,125" mean the same thing here as in PlaceThings.
public sealed class SandPlan : IAnchoredPlan
{
    // 0 clears, 1 is SandGrid.MaxDepth. Validated to that range by TryPlan.
    public double Depth = SetSandStep.DefaultDepth;
    public int Width;
    public int Height;

    public SceneAnchorKind Anchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorZ { get; set; }
    public int OffsetX { get; set; }
    public int OffsetZ { get; set; }
}
