using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// The game-touching halves of Select / OpenArchitect / ShowTextures. See
// Shared/Steps/BuiltIn/UiSteps.cs for the pure halves and for why the harness needed to be able to
// photograph the interface at all.

public sealed class SelectAction : IStepAction
{
    public string Type => SelectStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!SelectStep.TryPlan(args, out SelectionPlan plan, out string? error))
            return StepOutcome.Fail($"Select: {error}");

        ThingDef? def = DefDatabase<ThingDef>.GetNamedSilentFail(plan.DefName);
        if (def == null)
            return StepOutcome.Fail($"Select: no ThingDef named '{plan.DefName}'");

        List<Thing> present = ctx.Map.listerThings.ThingsOfDef(def);

        // Fail rather than select nothing. An empty selection draws no gizmos and no inspect pane,
        // so the frame comes back as a bare map -- indistinguishable from the step having worked and
        // the mod under test having drawn nothing, which is the failure this whole step exists to
        // make visible.
        if (present.Count == 0)
            return StepOutcome.Fail($"Select: no '{plan.DefName}' on the map to select");

        Find.Selector.ClearSelection();

        int wanted = plan.Count < present.Count ? plan.Count : present.Count;
        for (int i = 0; i < wanted; i++)
            Find.Selector.Select(present[i], playSound: false, forceDesignatorDeselect: false);

        // One frame, like the scene steps: the inspect pane and the gizmo row are laid out during
        // the next OnGUI, so a Screenshot on this same frame captures the map without them.
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}

public sealed class OpenArchitectAction : IStepAction
{
    public string Type => OpenArchitectStep.StepType;

    // MainTabWindow_Architect builds its own ArchitectCategoryTab list in the constructor and keeps
    // it private. We want THAT list's entry rather than a freshly constructed one, because the
    // window compares selectedDesPanel by reference when it highlights the open category -- a
    // look-alike renders the designator grid correctly and leaves the category button unlit, which
    // is a wrong frame that looks right.
    //
    // Pinned by an API test, because a rename here fails at runtime rather than at compile time.
    private const string CachedPanelsField = "desPanelsCached";

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        var window = MainButtonDefOf.Architect.TabWindow as MainTabWindow_Architect;
        if (window == null)
            return StepOutcome.Fail("OpenArchitect: the Architect main button has no MainTabWindow_Architect");

        Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Architect, playSound: false);

        if (!args.TryGetValue(OpenArchitectStep.CategoryArg, out string? categoryName))
            return Settled();

        DesignationCategoryDef? category =
            DefDatabase<DesignationCategoryDef>.GetNamedSilentFail(categoryName.Trim());
        if (category == null)
            return StepOutcome.Fail(
                $"OpenArchitect: no DesignationCategoryDef named '{categoryName.Trim()}'. " +
                $"Known: {KnownCategories()}");

        if (!TryFindPanel(window, category, out object? panel, out string? panelError))
            return StepOutcome.Fail($"OpenArchitect: {panelError}");

        window.selectedDesPanel = (ArchitectCategoryTab)panel!;
        return Settled();
    }

    // Two frames rather than the scene steps' one. The architect window is opened by SetCurrentTab
    // on this frame but only laid out and drawn on the next, and the designator grid inside it
    // resolves its icons as it draws -- so a capture one frame later can catch the panel mid-fill.
    private static StepOutcome Settled() => new StepOutcome { WaitFrames = 2 };

    private static bool TryFindPanel(MainTabWindow_Architect window, DesignationCategoryDef category,
                                     out object? panel, out string? error)
    {
        panel = null;

        FieldInfo? field = AccessTools.Field(typeof(MainTabWindow_Architect), CachedPanelsField);
        if (field == null)
        {
            error = $"MainTabWindow_Architect.{CachedPanelsField} no longer exists — " +
                    "vanilla renamed it and the harness needs updating";
            return false;
        }

        var panels = field.GetValue(window) as IEnumerable<ArchitectCategoryTab>;
        if (panels == null)
        {
            error = $"MainTabWindow_Architect.{CachedPanelsField} is no longer a list of " +
                    "ArchitectCategoryTab — the harness needs updating";
            return false;
        }

        panel = panels.FirstOrDefault(p => p != null && p.def == category);
        if (panel == null)
        {
            error = $"the architect menu has no panel for category '{category.defName}' " +
                    "(it exists as a def but nothing buildable is filed under it)";
            return false;
        }

        error = null;
        return true;
    }

    private static string KnownCategories() =>
        string.Join(", ", DefDatabase<DesignationCategoryDef>.AllDefs
                                                             .Select(d => d.defName)
                                                             .OrderBy(n => n));
}

public sealed class ShowTexturesAction : IStepAction
{
    public string Type => ShowTexturesStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        if (!ShowTexturesStep.TryPlan(args, out GalleryPlan plan, out string? error))
            return StepOutcome.Fail($"ShowTextures: {error}");

        // Replace rather than stack, so a scenario showing two galleries in turn photographs the
        // second one rather than the second one on top of the first.
        TextureGalleryWindow.CloseAll();
        Find.WindowStack.Add(new TextureGalleryWindow(plan));

        // Same two frames as the architect menu, for the same reason: the window is stacked now and
        // drawn next frame.
        return new StepOutcome { WaitFrames = 2 };
    }
}

// A grid of textures at a fixed pixel size, each labelled with the dimensions it actually loaded at.
//
// NOT A MOCK OF THE GAME'S UI. Every icon here is fetched with ContentFinder<Texture2D>.Get and
// drawn with Widgets.DrawTextureFitted -- the same two calls a Command, a Designator or an
// ITab makes, against the same Texture2D instance the game is holding. What the window removes is
// the need to navigate to whichever window happens to draw a given icon; what it adds is the
// measurement printed underneath, so the frame states its own evidence instead of inviting a
// judgement about sharpness.
public sealed class TextureGalleryWindow : Window
{
    private readonly GalleryPlan plan;

    // Fixed screen position rather than centred. A gallery in the same pixels on every run means an
    // A/B of two runs can be differenced over that rectangle and attributed; a centred window moves
    // with resolution and quietly breaks the comparison on a different monitor.
    private const float ScreenMargin = 60f;

    private const float LabelHeight = 20f;
    private const float CellPadding = 12f;
    private const float TitleHeight = 28f;

    public TextureGalleryWindow(GalleryPlan plan)
    {
        this.plan = plan;

        // Every one of these is load-bearing for a screenshot rather than cosmetic. forcePause and
        // absorbInputAroundWindow would both put this window in DialogGuard's blocking set, and the
        // harness would then suppress the very window the step just asked for -- a step that reports
        // success and shows nothing.
        forcePause = false;
        absorbInputAroundWindow = false;
        preventCameraMotion = false;
        closeOnAccept = false;
        closeOnCancel = true;
        doCloseX = true;
        draggable = true;
        drawShadow = true;
    }

    public override Vector2 InitialSize => new Vector2(
        plan.Columns * (plan.Size + CellPadding) + Margin * 2f,
        TitleHeight + plan.Rows * (plan.Size + LabelHeight + CellPadding) + Margin * 2f);

    protected override void SetInitialSizeAndPosition()
    {
        Vector2 size = InitialSize;
        windowRect = new Rect(ScreenMargin, ScreenMargin, size.x, size.y);
        windowRect = windowRect.Rounded();
    }

    public override void DoWindowContents(Rect inRect)
    {
        Text.Font = GameFont.Small;
        Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, TitleHeight),
                      $"ShowTextures — {plan.Paths.Count} at {plan.Size}px");

        float cellWidth = plan.Size + CellPadding;
        float cellHeight = plan.Size + LabelHeight + CellPadding;

        for (int i = 0; i < plan.Paths.Count; i++)
        {
            float x = inRect.x + (i % plan.Columns) * cellWidth;
            float y = inRect.y + TitleHeight + (i / plan.Columns) * cellHeight;
            DrawOne(plan.Paths[i], new Rect(x, y, plan.Size, plan.Size));
        }
    }

    private void DrawOne(string path, Rect rect)
    {
        // reportFailure: false -- a missing path is reported IN THE FRAME below, which is where
        // someone reading the gallery will look. Letting ContentFinder log an error as well would
        // trip the run's error gate over a scenario's own typo.
        Texture2D? texture = ContentFinder<Texture2D>.Get(path, reportFailure: false);

        Rect labelRect = new Rect(rect.x, rect.yMax, rect.width, LabelHeight);
        Text.Font = GameFont.Tiny;

        if (texture == null)
        {
            Widgets.DrawBox(rect);
            Widgets.Label(labelRect, "missing");
            Text.Font = GameFont.Small;
            return;
        }

        Widgets.DrawTextureFitted(rect, texture, 1f);

        // The measurement, not a caption. This is what makes the frame quotable: "32x32" under art
        // its author shipped at 128 is a fact, where "looks soft" is an argument.
        Widgets.Label(labelRect, $"{texture.width}x{texture.height}");
        Text.Font = GameFont.Small;
    }

    public static void CloseAll()
    {
        WindowStack stack = Find.WindowStack;
        if (stack == null)
            return;

        // Over a copy: closing a window mutates the stack mid-enumeration.
        List<Window> open = stack.Windows.Where(w => w is TextureGalleryWindow).ToList();
        for (int i = 0; i < open.Count; i++)
            open[i].Close(doCloseSound: false);
    }
}
