using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps;
using RimWorldTestHarness.Shared.Steps.BuiltIn;

namespace RimWorldTestHarness.Tests;

// Validation and planning for the three UI steps. The live halves need a running game — selection,
// a main tab, a window stack — so what is checkable offline is everything a scenario author can get
// wrong in the JSON, which is where the mistakes actually are.
[TestFixture]
public class UiStepTests
{
    private static Dictionary<string, string> Args(params (string Key, string Value)[] args)
    {
        Dictionary<string, string> dict = new Dictionary<string, string>();
        foreach ((string key, string value) in args)
            dict[key] = value;
        return dict;
    }

    // ---- Select -----------------------------------------------------------

    [Test]
    public void Select_RequiresADefName()
    {
        Assert.That(new SelectStep().TryValidate(Args(), out string? error), Is.False);
        Assert.That(error, Does.Contain("defName"));
    }

    [Test]
    public void Select_DefaultsToOneThing()
    {
        Assert.That(SelectStep.TryPlan(Args(("defName", "Muffalo")), out SelectionPlan plan, out _), Is.True);
        Assert.That(plan.DefName, Is.EqualTo("Muffalo"));
        Assert.That(plan.Count, Is.EqualTo(1));
    }

    [Test]
    public void Select_TrimsTheDefName()
    {
        // A trailing space in hand-written JSON would otherwise reach GetNamedSilentFail and come
        // back as "no ThingDef named 'Muffalo '", which reads as a missing mod rather than a typo.
        Assert.That(SelectStep.TryPlan(Args(("defName", "  Muffalo  ")), out SelectionPlan plan, out _), Is.True);
        Assert.That(plan.DefName, Is.EqualTo("Muffalo"));
    }

    [TestCase("0")]
    [TestCase("-3")]
    [TestCase("17")]
    public void Select_CountOutsideRange_IsRejected(string count)
    {
        Assert.That(SelectStep.TryPlan(Args(("defName", "Muffalo"), ("count", count)), out _, out string? error),
                    Is.False);
        Assert.That(error, Does.Contain("between 1 and 16"));
    }

    [Test]
    public void Select_UnknownArg_IsRejected()
    {
        // Silently ignoring `defname` would select nothing and photograph a bare map, which reads as
        // the mod under test drawing nothing.
        Assert.That(new SelectStep().TryValidate(Args(("defname", "Muffalo")), out string? error), Is.False);
        Assert.That(error, Does.Contain("unknown arg"));
    }

    // ---- OpenArchitect ----------------------------------------------------

    [Test]
    public void OpenArchitect_NoArgs_OpensTheCategoryList()
    {
        Assert.That(new OpenArchitectStep().TryValidate(Args(), out string? error), Is.True);
        Assert.That(error, Is.Null);
    }

    [Test]
    public void OpenArchitect_UnknownCategoryName_IsNotRejectedOffline()
    {
        // Deliberate: TryValidate must be pure, and DesignationCategoryDefs only exist once a game
        // has loaded defs. The step fails at execution instead, where it can list the real ones.
        Assert.That(new OpenArchitectStep().TryValidate(Args(("category", "NoSuchCategory")), out _), Is.True);
    }

    [Test]
    public void OpenArchitect_EmptyCategory_IsRejected()
    {
        Assert.That(new OpenArchitectStep().TryValidate(Args(("category", "  ")), out string? error), Is.False);
        Assert.That(error, Does.Contain("omit it"));
    }

    // ---- ShowTextures -----------------------------------------------------

    [Test]
    public void ShowTextures_RequiresPaths()
    {
        Assert.That(new ShowTexturesStep().TryValidate(Args(), out string? error), Is.False);
        Assert.That(error, Does.Contain("paths"));
    }

    [Test]
    public void ShowTextures_SplitsTrimsAndKeepsOrder()
    {
        Assert.That(ShowTexturesStep.TryPlan(
            Args(("paths", "UI/Icons/A; Things/B ;UI/C")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Paths, Is.EqualTo(new[] { "UI/Icons/A", "Things/B", "UI/C" }));
    }

    [Test]
    public void ShowTextures_DropsEmptyEntriesRatherThanFailing()
    {
        // A trailing separator is the likeliest typo in a hand-written list and the least
        // interesting thing to lose a whole run over.
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", "UI/A;;UI/B;")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Paths, Is.EqualTo(new[] { "UI/A", "UI/B" }));
    }

    [Test]
    public void ShowTextures_KeepsDuplicates()
    {
        // Asking for the same path twice is how you compare it against itself at two sizes in one
        // frame, so deduping would remove a use rather than protect one.
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", "UI/A;UI/A")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Paths.Count, Is.EqualTo(2));
    }

    [Test]
    public void ShowTextures_SeparatorOnly_IsRejected()
    {
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", " ; ; ")), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain("no paths"));
    }

    [Test]
    public void ShowTextures_DefaultsToGizmoSize()
    {
        // 75 is Gizmo.Height — the size at which "is this texture big enough" is actually decided.
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", "UI/A")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Size, Is.EqualTo(75));
    }

    [TestCase("4")]
    [TestCase("1024")]
    public void ShowTextures_SizeOutsideRange_IsRejected(string size)
    {
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", "UI/A"), ("size", size)), out _, out string? error),
                    Is.False);
        Assert.That(error, Does.Contain("between 8 and 512"));
    }

    [Test]
    public void ShowTextures_TooManyPaths_IsRejectedRatherThanTruncated()
    {
        // Truncating would produce a gallery missing exactly the icons at the end of the list, with
        // nothing in the frame to say so.
        string many = string.Join(";", Enumerable.Range(0, 65).Select(i => $"UI/Icon{i}"));
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", many)), out _, out string? error), Is.False);
        Assert.That(error, Does.Contain("65 entries"));
    }

    [Test]
    public void ShowTextures_ColumnsDefaultToTheRowLengthWhenShorter()
    {
        // Three icons should be a row of three, not three in a row of six with dead space that
        // shifts every later cell — the gallery's position is what an A/B differences over.
        Assert.That(ShowTexturesStep.TryPlan(Args(("paths", "UI/A;UI/B;UI/C")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Columns, Is.EqualTo(3));
        Assert.That(plan.Rows, Is.EqualTo(1));
    }

    [Test]
    public void ShowTextures_RowsRoundUp()
    {
        Assert.That(ShowTexturesStep.TryPlan(
            Args(("paths", "A;B;C;D;E;F;G"), ("columns", "3")), out GalleryPlan plan, out _), Is.True);
        Assert.That(plan.Rows, Is.EqualTo(3));
    }

    // ---- residue ----------------------------------------------------------

    // All three change interface state and nothing else. Under-reporting here is the dangerous
    // direction: a following scenario would photograph the map with the previous one's menu open,
    // which looks like a UI bug in the mod under test rather than like contamination.
    [TestCase(SelectStep.StepType)]
    [TestCase(OpenArchitectStep.StepType)]
    [TestCase(ShowTexturesStep.StepType)]
    public void UiSteps_DeclareUiResidueOnly(string stepType)
    {
        Assert.That(ScenarioResidueAnalyzer.OfStep(stepType), Is.EqualTo(ScenarioResidue.Ui));
    }

    // Ui is soft-resettable, which is what keeps a suite from reloading the save after every
    // screenshot of a menu. WorldStateReset.RestoreUi is the other half of this claim.
    [Test]
    public void UiResidue_IsSoftResettable()
    {
        Assert.That(ScenarioResidueAnalyzer.SoftResettable.HasFlag(ScenarioResidue.Ui), Is.True);
        Assert.That(ScenarioResidueAnalyzer.RequiresReload.HasFlag(ScenarioResidue.Ui), Is.False);
    }

    [Test]
    public void UiResidue_HasALabel()
    {
        // A flag with no label vanishes from the suite plan's residue line, which is the only place
        // a reader learns why a reload was or wasn't scheduled.
        Assert.That(ScenarioResidueAnalyzer.Describe(ScenarioResidue.Ui), Is.Not.Empty);
    }
}
