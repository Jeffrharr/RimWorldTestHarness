using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// The policy half of scene setup's `clear` arg: given one thing's *description* — its ThingCategory
// name and whether its def is destroyable — decide whether clearing may destroy it, and word what
// happened. No Unity/Verse dependency, so the whole policy is unit-testable offline and
// Mod/SceneBuilder.cs is left with nothing but the Thing.Destroy / RoofGrid.SetRoof calls themselves.
//
// Kept out of SceneLayout deliberately: that file answers "which cells", this one answers "what may
// be removed from a cell". The `clear` ARG is still read in SceneLayout, where all arg reading lives.
//
// Categories are passed as the ThingCategory enum's own member NAME rather than a mirrored enum. That
// keeps this table the single source of truth (the adapter does no branching at all, so it can't
// drift from it) at the cost of one ToString per thing, which is nothing next to the spawn work it
// precedes. ApiCompatibilityTests pins the vanilla member names so a rename fails loudly instead of
// quietly turning every category into "leave alone".
public static class SceneClearing
{
    public const string CategoryPawn = "Pawn";
    public const string CategoryBuilding = "Building";
    public const string CategoryPlant = "Plant";
    public const string CategoryItem = "Item";
    public const string CategoryFilth = "Filth";

    // A whitelist, not a blacklist. The point of clearing is to open up ground for a lighting
    // screenshot, so it covers exactly the four kinds of thing that physically sit on that ground:
    // mineable rock and existing structures (Building), trees and bushes (Plant), chunks and dropped
    // stuff (Item), and the mess left behind (Filth). Everything else — Projectile, Mote, Gas,
    // Attachment, Ethereal, PsychicEmitter — is transient decoration that neither blocks a build nor
    // shades a cell, and destroying it would be pure collateral damage.
    //
    // A whitelist also fails in the safe direction: a category RimWorld adds in a future version is
    // left alone rather than silently bulldozed, and if it *does* block a placement, PlaceThings'
    // GenSpawn.CanSpawnAt re-check still reports the cell rather than passing.
    private static readonly string[] ClearableCategories =
    {
        CategoryBuilding,
        CategoryPlant,
        CategoryItem,
        CategoryFilth,
    };

    // Blocker/leftover lists are capped in reports because SetTerrain's footprint can be 16384 cells:
    // an uncapped list would bury the run's actual verdict under thousands of near-identical entries.
    // The count is always stated in full, so nothing is hidden — only elided.
    public const int MaxReportedThings = 8;

    // Pawns are never destroyed. Not squeamishness: a colonist can wander onto the pad between the
    // scenario being written and the step running, so destroying one would make runs both destructive
    // AND nondeterministic. A pawn is also passable, so it never refuses a placement — it just gets
    // shoved out of the way — which means leaving it costs the scene nothing.
    public const string ReasonPawn = "scene setup never destroys pawns";

    public static ClearVerdict Classify(string category, bool destroyable, out string? reason)
    {
        if (System.Array.IndexOf(ClearableCategories, category) < 0)
        {
            reason = category == CategoryPawn
                ? ReasonPawn
                : $"a '{category}' thing neither blocks a build nor shades a cell";
            return ClearVerdict.Leave;
        }

        // ThingDef.destroyable is checked BEFORE calling Destroy rather than after: Thing.Destroy
        // Log.Errors on a non-destroyable thing and returns, so skipping this check would bury the
        // real diagnosis in Player.log instead of putting it in the run's report. Same reasoning as
        // SceneBuilder's DefDatabase lookups passing errorOnFail: false.
        if (!destroyable)
        {
            reason = "its def is flagged not destroyable";
            return ClearVerdict.Blocked;
        }

        reason = null;
        return ClearVerdict.Destroy;
    }

    // One line for Player.log saying what clearing actually did. Counts rather than prose, because
    // the interesting question when a screenshot looks wrong is "did it strip the roof or not".
    public static string Describe(ClearTally tally) =>
        $"cleared {tally.Cells} footprint cells: destroyed {tally.ThingsDestroyed} things, " +
        $"stripped roof from {tally.RoofedCells}";

    // The gate. A blocker is a thing that physically occupies the footprint and that clearing was
    // *unable* to remove, which is precisely the case where the scene is not what the scenario asked
    // for. Returning a reason here is what keeps the run from coming back green over a pad that is
    // still half-buried — the recurring failure class this whole harness exists to catch.
    public static string? BlockedError(ClearTally tally)
    {
        if (tally.Blocked.Count == 0)
            return null;

        return $"clear could not remove {tally.Blocked.Count} blocker(s) from the footprint: " +
               $"{FormatList(tally.Blocked)}";
    }

    // Appended to a refused-cell entry so the report names what is still standing there, instead of
    // leaving the author to guess whether the cell was rock, water or a wall they forgot about.
    public static string RefusalDetail(IReadOnlyList<string> blockersInCell) =>
        blockersInCell.Count == 0 ? "" : $" (uncleared: {FormatList(blockersInCell)})";

    // Capped join. Elides the tail rather than truncating mid-entry, so every entry shown is complete
    // and the elision is explicit.
    public static string FormatList(IReadOnlyList<string> items)
    {
        int shown = items.Count < MaxReportedThings ? items.Count : MaxReportedThings;
        List<string> parts = new List<string>();
        for (int i = 0; i < shown; i++)
            parts.Add(items[i]);

        if (items.Count > shown)
            parts.Add($"and {items.Count - shown} more");

        return string.Join(", ", parts);
    }
}

// What clearing is allowed to do with one thing.
public enum ClearVerdict
{
    // Destroyable and in the way: remove it.
    Destroy,

    // Deliberately spared (a pawn, or something transient that isn't in the way). Worth logging so
    // "clear ran and yet something is still there" is never a mystery, but not a failure.
    Leave,

    // Should have been removed and can't be. A real, reportable defect in the scene.
    Blocked,
}

// Running totals for one step's clearing pass. A plain accumulator in Shared/ so the adapter only
// increments it and the wording of every message stays in this file with the policy that produced it.
public sealed class ClearTally
{
    // Footprint cells inspected — counted whether or not `clear` was asked for, so the roofed-cell
    // warning has a denominator either way.
    public int Cells;

    // Cells that carried roof when the step arrived. When clearing ran, this is also how many roofs
    // were stripped (SetRoof is a no-op on an already-null cell); when it didn't, it is the warning.
    public int RoofedCells;

    public int ThingsDestroyed;

    // "(128,118) MineableGranite — its def is flagged not destroyable"
    public List<string> Blocked { get; } = new List<string>();

    // Same shape, for things clearing spared on purpose.
    public List<string> Left { get; } = new List<string>();
}
