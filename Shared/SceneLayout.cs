using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Turns scene-setup step args into plain geometry: which cells to fill, relative to an anchor. Pure
// string/number work with no Unity/Verse dependency, so the whole layout vocabulary is unit-testable
// offline and Mod/SceneBuilder.cs stays a thin adapter that only resolves defs and calls GenSpawn —
// the same split as ReportComparer/TimelapseExpander (see DESIGN.md).
//
// Why this exists at all: shadow_lean_equinox passed its probe but its screenshot was useless,
// because the fixture's flat sand has almost nothing tall enough to cast a shadow (TODO.md). A
// lattice of pillars on uniform ground makes shadow direction and length legible at a glance, which
// is what turns a day-cycle Timelapse from a green checkmark into something reviewable.
//
// Coordinates are ANCHOR-RELATIVE offsets, never absolute cells. That is what keeps this file pure:
// resolving "center" needs a live Map, so the adapter does it, and a plan computed here is valid for
// any map size. It also means one scenario works against both the committed fixture and the
// -quicktest throwaway colony, whose map dimensions differ.
public static class SceneLayout
{
    // Layout kinds. `grid` is the default because it's the shadow-caster case: separate pillars each
    // throw an independent shadow, and unlike a closed wall rectangle it can't be read as a room.
    public const string LayoutGrid = "grid";
    public const string LayoutRow = "row";
    public const string LayoutCells = "cells";

    public const string DefaultLayout = LayoutGrid;
    public const string DefaultRotation = "North";
    public const string AnchorCenter = "center";

    public const int DefaultCols = 4;
    public const int DefaultRows = 4;
    public const int DefaultSpacing = 5;
    public const int DefaultCount = 5;
    public const string DefaultAxis = AxisX;
    public const int DefaultTerrainWidth = 40;
    public const int DefaultTerrainHeight = 40;

    public const string AxisX = "x";
    public const string AxisZ = "z";

    // Guards a fat-fingered cols/rows/spacing from turning a test run into a several-thousand-thing
    // spawn that tanks the framerate the screenshots depend on. Mirrors TimelapseExpander.MaxFrames.
    public const int MaxPlacements = 512;

    // Terrain is painted as a rect rather than a cell list, so it gets a far higher cap than
    // MaxPlacements — repainting ground is cheap, spawning buildings is not.
    public const int MaxTerrainCells = 16384;

    // Rot4.FromString silently yields North for anything it doesn't recognise, so the valid set is
    // checked here instead: a "north" or "Noth" typo must fail the scenario, not quietly rotate
    // nothing. Order matches Rot4's own int values.
    private static readonly string[] ValidRotations = { "North", "East", "South", "West" };

    // Fog is lifted by default; see StepArgs.SceneUnfog for why an invisible-but-successful scene is
    // the failure mode this guards against.
    public const bool DefaultUnfog = true;

    // Clearing is opt-IN, where unfogging is opt-out. See StepArgs.SceneClear for the argument: fog is
    // a view, rock is content, and the same cores are reachable from a dev action pointed at a real
    // colony. SceneBuilder still warns about a roofed footprint when this is off, so choosing the safe
    // default doesn't buy silence.
    public const bool DefaultClear = false;

    private static readonly string[] CommonArgs =
    {
        StepArgs.SceneDef,
        StepArgs.SceneAnchor,
        StepArgs.SceneOffset,
        StepArgs.SceneUnfog,
        StepArgs.SceneClear,
    };

    // ----- PlaceThings ------------------------------------------------------------------------

    // Returns false with a specific reason rather than throwing, so a malformed scenario yields a
    // readable report entry instead of an exception during mod startup (which would leave
    // Runner/run_test.sh waiting on a report file that never appears).
    public static bool TryPlan(
        IReadOnlyDictionary<string, string> args,
        out ScenePlan plan,
        out string? error)
    {
        plan = new ScenePlan();

        string layout = ArgReader.ReadString(args, StepArgs.PlaceThingsLayout, DefaultLayout);
        if (!ValidateLayoutArgs(args, layout, out error))
            return false;
        if (!ReadDefName(args, out plan.DefName, out error))
            return false;
        if (!ReadAnchor(args, plan, out error))
            return false;
        if (!ReadStuffAndRotation(args, plan, out error))
            return false;
        if (!ArgReader.TryReadBool(args, StepArgs.SceneUnfog, DefaultUnfog, out plan.Unfog, out error))
            return false;
        if (!ArgReader.TryReadBool(args, StepArgs.SceneClear, DefaultClear, out plan.Clear, out error))
            return false;
        if (!ReadCells(args, layout, plan.Cells, out error))
            return false;

        return ValidateCells(plan.Cells, out error);
    }

    // The whitelist is layout-specific, not a union: `cols` under `layout: row` is rejected rather
    // than ignored. Same reasoning as TimelapseExpander's unknown-key check — an arg that looks
    // honoured but isn't produces a plausible-looking wrong scene, the worst failure for a
    // verification tool.
    private static bool ValidateLayoutArgs(
        IReadOnlyDictionary<string, string> args, string layout, out string? error)
    {
        if (!TryLayoutArgs(layout, out string[] layoutArgs, out error))
            return false;

        List<string> known = new List<string>(CommonArgs);
        known.Add(StepArgs.PlaceThingsLayout);
        known.Add(StepArgs.PlaceThingsStuff);
        known.Add(StepArgs.PlaceThingsRot);
        known.AddRange(layoutArgs);

        return ArgReader.ValidateKnownArgs(args, known.ToArray(), out error);
    }

    private static bool TryLayoutArgs(string layout, out string[] layoutArgs, out string? error)
    {
        switch (layout)
        {
            case LayoutGrid:
                layoutArgs = new[] { StepArgs.PlaceThingsCols, StepArgs.PlaceThingsRows, StepArgs.PlaceThingsSpacing };
                error = null;
                return true;
            case LayoutRow:
                layoutArgs = new[] { StepArgs.PlaceThingsCount, StepArgs.PlaceThingsSpacing, StepArgs.PlaceThingsAxis };
                error = null;
                return true;
            case LayoutCells:
                layoutArgs = new[] { StepArgs.PlaceThingsCells };
                error = null;
                return true;
            default:
                layoutArgs = new string[0];
                error = $"'{StepArgs.PlaceThingsLayout}' must be one of: " +
                        $"{LayoutGrid}, {LayoutRow}, {LayoutCells} (got '{layout}')";
                return false;
        }
    }

    private static bool ReadStuffAndRotation(
        IReadOnlyDictionary<string, string> args, ScenePlan plan, out string? error)
    {
        // Absent stuff means "let the adapter pick a default for a MadeFromStuff def", which is not
        // the same as an explicit empty string — hence null rather than "".
        plan.StuffDefName = args.TryGetValue(StepArgs.PlaceThingsStuff, out string? stuff) ? stuff : null;
        if (plan.StuffDefName != null && plan.StuffDefName.Length == 0)
        {
            error = $"'{StepArgs.PlaceThingsStuff}' must not be empty — omit it to use the def's default stuff";
            return false;
        }

        plan.Rotation = ArgReader.ReadString(args, StepArgs.PlaceThingsRot, DefaultRotation);
        if (Array.IndexOf(ValidRotations, plan.Rotation) < 0)
        {
            error = $"'{StepArgs.PlaceThingsRot}' must be one of: " +
                    $"{string.Join(", ", ValidRotations)} (got '{plan.Rotation}')";
            return false;
        }

        error = null;
        return true;
    }

    private static bool ReadCells(
        IReadOnlyDictionary<string, string> args, string layout,
        List<ScenePlacement> into, out string? error)
    {
        switch (layout)
        {
            case LayoutGrid:
                return ReadGridCells(args, into, out error);
            case LayoutRow:
                return ReadRowCells(args, into, out error);
            default:
                return ReadExplicitCells(args, into, out error);
        }
    }

    private static bool ReadGridCells(
        IReadOnlyDictionary<string, string> args, List<ScenePlacement> into, out string? error)
    {
        if (!ReadPositive(args, StepArgs.PlaceThingsCols, DefaultCols, out int cols, out error))
            return false;
        if (!ReadPositive(args, StepArgs.PlaceThingsRows, DefaultRows, out int rows, out error))
            return false;
        if (!ReadPositive(args, StepArgs.PlaceThingsSpacing, DefaultSpacing, out int spacing, out error))
            return false;
        if (!CheckPlacementCount((long)cols * rows, out error))
            return false;

        // Integer-divided half-span, so an even column count lands one cell off true centre rather
        // than on a fractional cell. Deterministic and integral beats exactly centred here: cells
        // are discrete, and the sweep only needs the lattice near the camera's focus.
        int halfX = (cols - 1) * spacing / 2;
        int halfZ = (rows - 1) * spacing / 2;

        for (int ix = 0; ix < cols; ix++)
        {
            for (int iz = 0; iz < rows; iz++)
                into.Add(new ScenePlacement(ix * spacing - halfX, iz * spacing - halfZ));
        }

        error = null;
        return true;
    }

    private static bool ReadRowCells(
        IReadOnlyDictionary<string, string> args, List<ScenePlacement> into, out string? error)
    {
        if (!ReadPositive(args, StepArgs.PlaceThingsCount, DefaultCount, out int count, out error))
            return false;
        if (!ReadPositive(args, StepArgs.PlaceThingsSpacing, DefaultSpacing, out int spacing, out error))
            return false;
        if (!CheckPlacementCount(count, out error))
            return false;

        string axis = ArgReader.ReadString(args, StepArgs.PlaceThingsAxis, DefaultAxis);
        if (axis != AxisX && axis != AxisZ)
        {
            error = $"'{StepArgs.PlaceThingsAxis}' must be '{AxisX}' or '{AxisZ}' (got '{axis}')";
            return false;
        }

        int half = (count - 1) * spacing / 2;
        for (int i = 0; i < count; i++)
        {
            int d = i * spacing - half;
            into.Add(axis == AxisX ? new ScenePlacement(d, 0) : new ScenePlacement(0, d));
        }

        error = null;
        return true;
    }

    // The escape hatch: "0,0; 4,0; 8,-3". Semicolon-separated because comma is already the pair
    // separator, and a scenario author writing an irregular arrangement shouldn't have to fight
    // nested delimiters.
    private static bool ReadExplicitCells(
        IReadOnlyDictionary<string, string> args, List<ScenePlacement> into, out string? error)
    {
        if (!args.TryGetValue(StepArgs.PlaceThingsCells, out string? raw))
        {
            error = $"'{StepArgs.PlaceThingsCells}' is required when " +
                    $"'{StepArgs.PlaceThingsLayout}' is '{LayoutCells}'";
            return false;
        }

        string[] entries = raw.Split(';');
        foreach (string entry in entries)
        {
            string trimmed = entry.Trim();
            // Tolerating a trailing ';' costs nothing and spares a confusing error on a list that
            // was obviously meant to parse.
            if (trimmed.Length > 0)
            {
                if (!ArgReader.TryReadIntPair(trimmed, StepArgs.PlaceThingsCells, out int dx, out int dz, out error))
                    return false;
                into.Add(new ScenePlacement(dx, dz));
            }
        }

        if (into.Count == 0)
        {
            error = $"'{StepArgs.PlaceThingsCells}' listed no cells (got '{raw}')";
            return false;
        }

        return CheckPlacementCount(into.Count, out error);
    }

    // Two things on one cell is always a spec bug: GenSpawn's WipeMode.Vanish would destroy the
    // first to place the second, so the scenario would silently spawn fewer casters than it reads
    // as spawning.
    private static bool ValidateCells(List<ScenePlacement> cells, out string? error)
    {
        HashSet<ScenePlacement> seen = new HashSet<ScenePlacement>();
        foreach (ScenePlacement cell in cells)
        {
            if (!seen.Add(cell))
            {
                error = $"duplicate cell offset ({cell.Dx},{cell.Dz}) — each placement needs its own cell";
                return false;
            }
        }

        error = null;
        return true;
    }

    // Takes a long because the grid path multiplies two args together: cols and rows are each only
    // checked for >= 1, so an int product could overflow into a small positive number, pass the cap
    // and leave the caller looping billions of times.
    private static bool CheckPlacementCount(long count, out string? error)
    {
        if (count > MaxPlacements)
        {
            error = $"{count} placements exceeds the {MaxPlacements}-placement cap";
            return false;
        }

        error = null;
        return true;
    }

    // ----- SetTerrain -------------------------------------------------------------------------

    // Uniform ground under the casters, so shadow contrast reads consistently instead of fighting
    // whatever biome texture the fixture happens to have. Kept a rect (not a cell list) so a 40x40
    // paint doesn't have to fit under MaxPlacements.
    public static bool TryPlanTerrain(
        IReadOnlyDictionary<string, string> args,
        out TerrainPlan plan,
        out string? error)
    {
        plan = new TerrainPlan();

        string[] known = new List<string>(CommonArgs)
        {
            StepArgs.SetTerrainWidth,
            StepArgs.SetTerrainHeight,
        }.ToArray();

        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;
        if (!ReadDefName(args, out plan.DefName, out error))
            return false;
        if (!ReadAnchor(args, plan, out error))
            return false;
        if (!ArgReader.TryReadBool(args, StepArgs.SceneUnfog, DefaultUnfog, out plan.Unfog, out error))
            return false;
        if (!ArgReader.TryReadBool(args, StepArgs.SceneClear, DefaultClear, out plan.Clear, out error))
            return false;
        if (!ReadPositive(args, StepArgs.SetTerrainWidth, DefaultTerrainWidth, out plan.Width, out error))
            return false;
        if (!ReadPositive(args, StepArgs.SetTerrainHeight, DefaultTerrainHeight, out plan.Height, out error))
            return false;

        // Multiplied as long: two large-but-legal ints (each only checked for >= 1) can overflow an
        // int product into a small positive number, sail past the cap and then hand SceneBuilder a
        // CellRect with billions of cells to iterate.
        if ((long)plan.Width * plan.Height > MaxTerrainCells)
        {
            error = $"{plan.Width}x{plan.Height} exceeds the {MaxTerrainCells}-cell terrain cap";
            return false;
        }

        error = null;
        return true;
    }

    // ----- LookAt -----------------------------------------------------------------------------

    // Placing casters at map centre is pointless if the camera is still wherever the save left it,
    // so aiming the camera is its own step rather than a side effect of PlaceThings — one step, one
    // responsibility, and a scenario can re-aim between screenshots.
    public static bool TryPlanLookAt(
        IReadOnlyDictionary<string, string> args,
        out LookAtPlan plan,
        out string? error)
    {
        plan = new LookAtPlan();

        // Listed explicitly rather than derived from CommonArgs: `def` is meaningless for a camera
        // move, and accepting it silently would suggest LookAt cares what it's looking at.
        string[] known = { StepArgs.SceneAnchor, StepArgs.SceneOffset, StepArgs.LookAtZoom };

        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;
        if (!ReadAnchor(args, plan, out error))
            return false;

        if (args.ContainsKey(StepArgs.LookAtZoom))
        {
            if (!ArgReader.TryReadDouble(args, StepArgs.LookAtZoom, 0, out double zoom, out error))
                return false;
            if (zoom <= 0)
            {
                error = $"'{StepArgs.LookAtZoom}' must be greater than 0 (got {ArgReader.Format(zoom)})";
                return false;
            }

            plan.Zoom = zoom;
        }

        error = null;
        return true;
    }

    // ----- shared arg reading -----------------------------------------------------------------

    private static bool ReadDefName(
        IReadOnlyDictionary<string, string> args, out string defName, out string? error)
    {
        defName = ArgReader.ReadString(args, StepArgs.SceneDef, "");
        if (string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{StepArgs.SceneDef}' is required and must not be empty";
            return false;
        }

        error = null;
        return true;
    }

    // "center" (the default) or an explicit "x,z". Defaulting to centre is what makes a scenario
    // fixture-independent: it needs no knowledge of where the save's colony happens to sit.
    //
    // internal, not private: PawnLayout reuses this exact anchor grammar so SpawnPawn and
    // PlaceThings can't drift on what "center"/"offset" mean.
    internal static bool ReadAnchor(
        IReadOnlyDictionary<string, string> args, IAnchoredPlan plan, out string? error)
    {
        string anchor = ArgReader.ReadString(args, StepArgs.SceneAnchor, AnchorCenter);
        if (anchor == AnchorCenter)
        {
            plan.Anchor = SceneAnchorKind.MapCenter;
        }
        else
        {
            if (!ArgReader.TryReadIntPair(anchor, StepArgs.SceneAnchor, out int ax, out int az, out error))
                return false;
            if (ax < 0 || az < 0)
            {
                error = $"'{StepArgs.SceneAnchor}' must be \"{AnchorCenter}\" or non-negative map " +
                        $"coordinates like \"125,125\" (got '{anchor}')";
                return false;
            }

            plan.Anchor = SceneAnchorKind.Absolute;
            plan.AnchorX = ax;
            plan.AnchorZ = az;
        }

        if (args.TryGetValue(StepArgs.SceneOffset, out string? offset))
        {
            if (!ArgReader.TryReadIntPair(offset, StepArgs.SceneOffset, out int dx, out int dz, out error))
                return false;
            plan.OffsetX = dx;
            plan.OffsetZ = dz;
        }

        error = null;
        return true;
    }

    // internal, not private: PawnLayout reuses this for its count/spacing args so the "must be at
    // least 1" rule and its error shape stay identical across the two planners.
    internal static bool ReadPositive(
        IReadOnlyDictionary<string, string> args, string key, int fallback,
        out int value, out string? error)
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

// Where a plan sits on the map. Resolved by the adapter, which is the only place that can see a
// live Map's dimensions.
public enum SceneAnchorKind
{
    MapCenter,
    Absolute,
}

// Shared by all three plan types so ReadAnchor is written once. An interface rather than a base
// class because the plans have nothing else in common.
public interface IAnchoredPlan
{
    SceneAnchorKind Anchor { get; set; }
    int AnchorX { get; set; }
    int AnchorZ { get; set; }
    int OffsetX { get; set; }
    int OffsetZ { get; set; }
}

// One cell to fill, as an offset from the resolved anchor. IEquatable is implemented explicitly so
// SceneLayout's duplicate detection can use a plain HashSet without falling back to ValueType's
// reflection-based structural comparison.
public readonly struct ScenePlacement : IEquatable<ScenePlacement>
{
    public readonly int Dx;
    public readonly int Dz;

    public ScenePlacement(int dx, int dz)
    {
        Dx = dx;
        Dz = dz;
    }

    public bool Equals(ScenePlacement other) => Dx == other.Dx && Dz == other.Dz;

    public override bool Equals(object? obj) => obj is ScenePlacement other && Equals(other);

    public override int GetHashCode() => (Dx * 397) ^ Dz;

    public override string ToString() => $"({Dx},{Dz})";
}

public sealed class ScenePlan : IAnchoredPlan
{
    public string DefName = "";

    // null means "the adapter picks the def's default stuff if it needs one".
    public string? StuffDefName;

    // Validated against Rot4's four names by SceneLayout, so the adapter can call Rot4.FromString
    // without a fallback path.
    public string Rotation = SceneLayout.DefaultRotation;

    // Lift fog over the map after building, so the scene is actually visible. See StepArgs.SceneUnfog.
    public bool Unfog = SceneLayout.DefaultUnfog;

    // Clear the PLACEMENT CELLS (not a bounding rect) of things and roof before building. Cell-by-cell
    // because a grid's pillars are spaced apart, and clearing the gaps between them would flatten
    // scenery the scenario never asked to touch. See StepArgs.SceneClear.
    public bool Clear = SceneLayout.DefaultClear;

    public SceneAnchorKind Anchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorZ { get; set; }
    public int OffsetX { get; set; }
    public int OffsetZ { get; set; }

    public List<ScenePlacement> Cells { get; } = new List<ScenePlacement>();
}

public sealed class TerrainPlan : IAnchoredPlan
{
    public string DefName = "";

    // Lift fog over the map after painting. See StepArgs.SceneUnfog.
    public bool Unfog = SceneLayout.DefaultUnfog;

    // Clear the WHOLE rect of things and roof before painting — every cell, not just the ones a pillar
    // will stand on. The pad is what shadows fall on, so overhead mountain roof darkens it just as
    // wrongly where nothing stands. See StepArgs.SceneClear.
    public bool Clear = SceneLayout.DefaultClear;

    public SceneAnchorKind Anchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorZ { get; set; }
    public int OffsetX { get; set; }
    public int OffsetZ { get; set; }

    public int Width;
    public int Height;
}

public sealed class LookAtPlan : IAnchoredPlan
{
    public SceneAnchorKind Anchor { get; set; }
    public int AnchorX { get; set; }
    public int AnchorZ { get; set; }
    public int OffsetX { get; set; }
    public int OffsetZ { get; set; }

    // null leaves the camera's current zoom alone.
    public double? Zoom;
}
