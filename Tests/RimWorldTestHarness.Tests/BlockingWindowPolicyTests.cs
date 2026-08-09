using NUnit.Framework;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Offline tests for the dialog-dismissal decision (Shared/BlockingWindowPolicy). The live half
// (Mod/DialogGuard) needs Verse and cannot run here; what is testable offline is the classification,
// which is the part with the judgement calls in it.
[TestFixture]
public class BlockingWindowPolicyTests
{
    private const string SomeDialog = "RimWorld.Dialog_NamePlayerColony";

    [Test]
    public void OrdinarySharedWindow_IsNotBlocking()
    {
        // A main tab or an inspector neither pauses nor eats input, so it cannot strand a run and must
        // be left alone. Dismissing these would be actively harmful — a scenario's screenshots would
        // stop showing UI the author put there on purpose.
        Assert.That(BlockingWindowPolicy.IsBlocking(forcePause: false, absorbInputAroundWindow: false),
            Is.False);
    }

    [Test]
    public void ForcePause_IsBlocking()
    {
        // The one that actually strands a run: forcePause stops TicksGame, so a FastForward step waits
        // for a tick target that can never arrive and the runner kills the run at 900s.
        Assert.That(BlockingWindowPolicy.IsBlocking(forcePause: true, absorbInputAroundWindow: false),
            Is.True);
    }

    [Test]
    public void AbsorbingInput_IsBlocking()
    {
        Assert.That(BlockingWindowPolicy.IsBlocking(forcePause: false, absorbInputAroundWindow: true),
            Is.True);
    }

    [Test]
    public void HarnessOwnedWindow_IsLeftAlone_EvenWhenBlocking()
    {
        // The harness must never close its own windows, or it would fight itself. Matched by namespace
        // so a window added later is covered without anyone remembering to extend a list.
        Assert.That(
            BlockingWindowPolicy.Decide("RimWorldTestHarness.Mod.SomeHarnessWindow",
                forcePause: true, absorbInputAroundWindow: true, isGiveNameDialog: false),
            Is.EqualTo(BlockingWindowPolicy.Decision.Leave));
    }

    [Test]
    public void BlockingNamingDialog_IsAcceptedRatherThanClosed()
    {
        // The distinction that matters. Dialog_GiveName is raised BECAUSE something has no name, so
        // force-closing it leaves that true and the game may raise it again on a later tick — the run
        // then stalls a second time and the "fix" looks flaky rather than wrong.
        Assert.That(
            BlockingWindowPolicy.Decide(SomeDialog,
                forcePause: true, absorbInputAroundWindow: true, isGiveNameDialog: true),
            Is.EqualTo(BlockingWindowPolicy.Decision.AcceptWithGeneratedName));
    }

    [Test]
    public void BlockingNonNamingDialog_IsClosed()
    {
        // A faction-selection prompt from a Vanilla Expanded mod, an error message box: nothing to
        // supply, so any dismissal will do.
        Assert.That(
            BlockingWindowPolicy.Decide("VanillaExpanded.Dialog_ChooseFactions",
                forcePause: true, absorbInputAroundWindow: false, isGiveNameDialog: false),
            Is.EqualTo(BlockingWindowPolicy.Decision.ForceClose));
    }

    [Test]
    public void NonBlockingNamingDialog_IsStillLeftAlone()
    {
        // Ordering check: the blocking test comes first, so a naming dialog that somehow does not
        // block is not dismissed just for being a naming dialog. The harness only ever intervenes to
        // unstick a run.
        Assert.That(
            BlockingWindowPolicy.Decide(SomeDialog,
                forcePause: false, absorbInputAroundWindow: false, isGiveNameDialog: true),
            Is.EqualTo(BlockingWindowPolicy.Decision.Leave));
    }

    [Test]
    public void FallbackName_IsFixedRatherThanRandom()
    {
        // A colony name is DRAWN ON SCREEN, and screenshots are compared pixel-for-pixel between runs.
        // A randomised name would put a different string in the frame each run and light up a CIELAB
        // diff for a reason unrelated to the change under test.
        Assert.That(BlockingWindowPolicy.FallbackName, Is.EqualTo("HarnessColony"));
        Assert.That(BlockingWindowPolicy.FallbackName, Is.Not.Empty);
    }

    [Test]
    public void ResolveName_KeepsWhateverTheDialogAlreadyOffered()
    {
        // RimWorld pre-fills these. The prompt that stranded indoor_glow_lamp.json arrived with both
        // boxes already filled, so accepting them goes through the same validation a player's click
        // would — and, in the two-field variant, keeps the faction and settlement names distinct.
        Assert.That(BlockingWindowPolicy.ResolveName("Elegance Township"), Is.EqualTo("Elegance Township"));
    }

    [Test]
    public void ResolveName_TrimsBeforeDeciding()
    {
        Assert.That(BlockingWindowPolicy.ResolveName("  Elegance Township  "), Is.EqualTo("Elegance Township"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ResolveName_FallsBackOnlyWhenTheBoxIsEmpty(string? existing)
    {
        // Whitespace counts as empty because Dialog_GiveName trims before validating, so a
        // spaces-only name would fail IsValidName and re-prompt.
        Assert.That(BlockingWindowPolicy.ResolveName(existing), Is.EqualTo(BlockingWindowPolicy.FallbackName));
    }

    [Test]
    public void NullTypeName_DoesNotThrow()
    {
        // Type.FullName is null for a few exotic constructions (generic parameters). A crash inside the
        // per-frame guard would take down the whole run, which is strictly worse than the stall it is
        // there to prevent.
        Assert.That(() => BlockingWindowPolicy.Decide(null!, true, true, false), Throws.Nothing);
        Assert.That(BlockingWindowPolicy.IsHarnessOwned(null!), Is.False);
    }
}
