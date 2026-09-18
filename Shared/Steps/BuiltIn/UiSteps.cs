using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Three steps that put INTERFACE on the screen, because until now a scenario could only photograph
// the map.
//
// WHY THIS IS A GAP WORTH CLOSING. A screenshot is the harness's answer to "a probe can pass while
// nothing reaches the screen" — but every existing step aims at the map, and a large class of mod
// output never appears there. Gizmos, the architect menu, ability and command buttons, icon art:
// all of it is drawn in UI space, none of it is reachable, and a change that breaks it passes every
// probe AND every screenshot. That is precisely the shape of failure the visual channel exists to
// catch, so leaving the UI unphotographable made the channel look more complete than it was.
//
// The case that prompted them: RAMSaver caps texture resolution at load and restores it when
// something draws the texture large. Its UI half was untestable here — the affected surfaces all
// need a mouse click to open, and a mouse click is the one thing a tick-driven scenario cannot make.
// So these steps reach the same state the click would, through the game's own API.
//
// ALL THREE ARE INVISIBLE TO A HIDDEN-UI SCREENSHOT. `Screenshot` defaults to `hideUi: true`, which
// sets vanilla's screenshotMode and blanks every window, gizmo and button — including everything
// these steps just arranged. A scenario using them must pass `hideUi: false`, and a frame that comes
// back as a bare map is the symptom of forgetting. That interaction is the single easiest way to get
// a green run that photographed nothing.

// Select — put things in the selection, so their inspect pane and gizmos draw.
//
// Selection is how RimWorld decides to render a gizmo row at all: Command_Ability, Designator,
// mod-added commands and the inspect pane are all drawn for the current selection and nowhere else.
// So this is the step that makes a mod's buttons photographable, and it is deliberately by DEF
// rather than by cell — a scenario that spawns three of something with PlaceThings should not then
// have to know where the scene builder put them.
public sealed class SelectStep : IStepSpec
{
    public const string StepType = "Select";
    public const string DefNameArg = "defName";
    public const string CountArg = "count";

    public const int DefaultCount = 1;

    // A multi-selection draws the gizmos COMMON to everything selected, so asking for more than a
    // handful usually photographs fewer buttons rather than more. The cap is here to make that a
    // rejected scenario rather than a puzzling frame.
    public const int MaxCount = 16;

    public string Type => StepType;

    // Ui, and nothing else. Selecting changes no world state — but it does persist into whatever
    // runs next, and a following scenario photographing "the map" with somebody's inspect pane open
    // over it is exactly the quiet contamination the residue flags exist to prevent.
    public ScenarioResidue Residue => ScenarioResidue.Ui;

    // Safe on a real colony: it moves the player's selection, which one click undoes, and touches
    // nothing else. The live channel's promise is to not bulldoze anything, not to never be noticed.
    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    public static bool TryPlan(IReadOnlyDictionary<string, string> args,
                               out SelectionPlan plan, out string? error)
    {
        plan = new SelectionPlan();

        string[] known = { DefNameArg, CountArg };
        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;

        if (!args.TryGetValue(DefNameArg, out string? defName) || string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{DefNameArg}' is required (a ThingDef defName — 'Human', 'Muffalo', 'Wall')";
            return false;
        }

        plan.DefName = defName.Trim();

        if (!ArgReader.TryReadInt(args, CountArg, DefaultCount, out plan.Count, out error))
            return false;

        if (plan.Count < 1 || plan.Count > MaxCount)
        {
            error = $"'{CountArg}' must be between 1 and {MaxCount} (got {plan.Count}); " +
                    "a larger selection draws fewer gizmos, not more, since only the common ones show";
            return false;
        }

        error = null;
        return true;
    }
}

public sealed class SelectionPlan
{
    public string DefName = "";
    public int Count = SelectStep.DefaultCount;
}

// OpenArchitect — open the architect menu, and optionally one of its categories.
//
// The category panel is where a buildable's `uiIcon` is drawn at full gizmo size, which makes it the
// only place in the game that shows what a ThingDef's art looks like as an ICON rather than as a map
// texture. Those are the same Texture2D (BuildableDef.ResolveIcon assigns
// `uiIcon = (Texture2D)material.mainTexture`), so anything that changes how a texture is loaded
// shows up here first and on the map second.
public sealed class OpenArchitectStep : IStepSpec
{
    public const string StepType = "OpenArchitect";
    public const string CategoryArg = "category";

    public string Type => StepType;

    public ScenarioResidue Residue => ScenarioResidue.Ui;

    // Opening a menu on someone's live colony is as invasive as pressing Escape afterwards.
    public bool LiveCallable => true;

    // Deliberately no def lookup here: TryValidate must be pure, and a DesignationCategoryDef only
    // exists once the game has loaded defs. A bad category name fails at execution with the list of
    // real ones, which is a better error than "unknown" would be anyway.
    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        string[] known = { CategoryArg };
        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;

        if (args.TryGetValue(CategoryArg, out string? category) && string.IsNullOrWhiteSpace(category))
        {
            error = $"'{CategoryArg}' was given but empty — omit it to open the category list itself";
            return false;
        }

        error = null;
        return true;
    }
}

// ShowTextures — draw named textures at a chosen pixel size, with their real dimensions printed
// underneath.
//
// This is the instrument for anything that changes what a texture IS rather than where it is drawn:
// resolution, format, atlas membership, a loader swap. It is not a mock of the UI — it fetches
// through `ContentFinder<Texture2D>.Get` and draws through `Widgets.DrawTextureFitted`, the same two
// calls a gizmo makes, on the same Texture2D object the game is holding. What it removes is the
// need to reach the particular window that happens to draw that icon.
//
// The printed dimensions are the point as much as the picture is. A soft icon in a frame is an
// opinion; "32x32" under an icon the mod's author shipped at 128 is a measurement, and it survives
// being looked at on a phone.
public sealed class ShowTexturesStep : IStepSpec
{
    public const string StepType = "ShowTextures";
    public const string PathsArg = "paths";
    public const string SizeArg = "size";
    public const string ColumnsArg = "columns";

    // Gizmo.Height. The size a command button, an ability and an architect entry all draw at, so it
    // is the size at which "is this texture big enough" actually gets decided.
    public const int DefaultSize = 75;

    public const int MinSize = 8;
    public const int MaxSize = 512;

    public const int DefaultColumns = 6;
    public const int MaxColumns = 16;

    // Past this the window stops fitting on a 1080p screen, and a gallery that runs off the bottom
    // of the frame is a screenshot that silently omits half its evidence.
    public const int MaxPaths = 64;

    // Same separator as SpawnPawn's hediff list. Texture paths contain '/' and frequently '_', but
    // never ';'.
    public const char PathSeparator = ';';

    public string Type => StepType;

    public ScenarioResidue Residue => ScenarioResidue.Ui;

    public bool LiveCallable => true;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        TryPlan(args, out _, out error);

    public static bool TryPlan(IReadOnlyDictionary<string, string> args,
                               out GalleryPlan plan, out string? error)
    {
        plan = new GalleryPlan();

        string[] known = { PathsArg, SizeArg, ColumnsArg };
        if (!ArgReader.ValidateKnownArgs(args, known, out error))
            return false;

        if (!args.TryGetValue(PathsArg, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = $"'{PathsArg}' is required (';'-separated ContentFinder paths, " +
                    "e.g. \"UI/Icons/Misc/Foo; Things/Item/Bar\")";
            return false;
        }

        plan.Paths = SplitPaths(raw);

        if (plan.Paths.Count == 0)
        {
            error = $"'{PathsArg}' contained no paths (got '{raw}')";
            return false;
        }

        if (plan.Paths.Count > MaxPaths)
        {
            error = $"'{PathsArg}' has {plan.Paths.Count} entries, more than the {MaxPaths} that fit " +
                    "on screen; a gallery taller than the frame drops evidence silently";
            return false;
        }

        if (!ArgReader.TryReadInt(args, SizeArg, DefaultSize, out plan.Size, out error))
            return false;

        if (plan.Size < MinSize || plan.Size > MaxSize)
        {
            error = $"'{SizeArg}' must be between {MinSize} and {MaxSize} pixels (got {plan.Size})";
            return false;
        }

        int defaultColumns = plan.Paths.Count < DefaultColumns ? plan.Paths.Count : DefaultColumns;
        if (!ArgReader.TryReadInt(args, ColumnsArg, defaultColumns, out plan.Columns, out error))
            return false;

        if (plan.Columns < 1 || plan.Columns > MaxColumns)
        {
            error = $"'{ColumnsArg}' must be between 1 and {MaxColumns} (got {plan.Columns})";
            return false;
        }

        error = null;
        return true;
    }

    // Empty entries are dropped rather than rejected: a trailing separator is the most likely typo
    // in a hand-written list and the least interesting one to fail a whole run over. Duplicates are
    // KEPT — asking for the same path twice is a legitimate way to compare it against itself across
    // two sizes in one frame.
    public static List<string> SplitPaths(string raw)
    {
        var paths = new List<string>();
        foreach (string piece in raw.Split(PathSeparator))
        {
            string trimmed = piece.Trim();
            if (trimmed.Length > 0)
                paths.Add(trimmed);
        }
        return paths;
    }
}

public sealed class GalleryPlan
{
    public List<string> Paths = new List<string>();
    public int Size = ShowTexturesStep.DefaultSize;
    public int Columns = ShowTexturesStep.DefaultColumns;

    public int Rows => (Paths.Count + Columns - 1) / Columns;
}
