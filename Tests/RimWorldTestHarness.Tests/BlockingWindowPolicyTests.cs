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

    // --- --keep-dialogs (HarnessRuntime.KeepDialogs / run_test.sh --keep-dialogs) ---

    // The flag's whole job: a window the guard would have closed stays up instead, so a Screenshot
    // step can photograph it.
    [Test]
    public void KeepDialogs_LeavesAWindowTheGuardWouldHaveClosed()
    {
        Assert.That(
            BlockingWindowPolicy.Decide("SomeMod.Dialog_Whatever", forcePause: true,
                absorbInputAroundWindow: true, isGiveNameDialog: false, keepDialogs: true),
            Is.EqualTo(BlockingWindowPolicy.Decision.LeaveUnderKeepDialogs));
    }

    // Naming dialogs are kept too, and this is the case that makes the flag dangerous — they are the
    // ones that genuinely strand a run. Uniformity is the point: "the harness does not touch your
    // windows" is a rule somebody can hold in their head at 2am, and "the harness does not touch your
    // windows except the naming ones" is not.
    [Test]
    public void KeepDialogs_LeavesNamingDialogsToo()
    {
        Assert.That(
            BlockingWindowPolicy.Decide("Verse.Dialog_NamePlayerFactionAndSettlement", forcePause: true,
                absorbInputAroundWindow: false, isGiveNameDialog: true, keepDialogs: true),
            Is.EqualTo(BlockingWindowPolicy.Decision.LeaveUnderKeepDialogs));
    }

    // IT IS A DISTINCT VALUE FROM Leave, and that is not pedantry. The adapter treats both as "do
    // nothing", so the only thing separating them is what gets REPORTED — and the report is the only
    // thing that turns a stalled --keep-dialogs run into a one-line diagnosis rather than the 900s
    // mystery this class was written to end.
    [Test]
    public void KeepDialogs_IsDistinguishableFromAWindowThatNeverBlocked()
    {
        BlockingWindowPolicy.Decision harmless = BlockingWindowPolicy.Decide(
            "Verse.MainTabWindow_Inspect", forcePause: false, absorbInputAroundWindow: false,
            isGiveNameDialog: false, keepDialogs: true);

        Assert.That(harmless, Is.EqualTo(BlockingWindowPolicy.Decision.Leave));
        Assert.That(harmless, Is.Not.EqualTo(BlockingWindowPolicy.Decision.LeaveUnderKeepDialogs));
    }

    // A harness-owned window is left alone for its own reason, and must not be reported as though the
    // flag were what saved it — otherwise every --keep-dialogs run names harness windows in
    // KeptDialogs and the field stops being a short list worth reading.
    [Test]
    public void KeepDialogs_DoesNotClaimCreditForHarnessOwnedWindows()
    {
        Assert.That(
            BlockingWindowPolicy.Decide("RimWorldTestHarness.Mod.SomeWindow", forcePause: true,
                absorbInputAroundWindow: true, isGiveNameDialog: false, keepDialogs: true),
            Is.EqualTo(BlockingWindowPolicy.Decision.Leave));
    }

    // The default has to stay "guard on". This is what keeps every existing scenario safe if the
    // parameter ever gains a caller that forgets to pass it.
    [Test]
    public void WithoutKeepDialogs_TheGuardStillActs()
    {
        Assert.That(
            BlockingWindowPolicy.Decide("SomeMod.Dialog_Whatever", true, true, false, keepDialogs: false),
            Is.EqualTo(BlockingWindowPolicy.Decision.ForceClose));
        Assert.That(
            BlockingWindowPolicy.Decide("SomeMod.Dialog_Whatever", true, true, false),
            Is.EqualTo(BlockingWindowPolicy.Decision.ForceClose),
            "the optional parameter must default to guarding, not to keeping");
    }

    // ActsOnWindow is what the adapter branches on, so it has to agree with the enum exactly. Written
    // as an exhaustive table rather than as two asserts because the failure it guards against is a
    // FIFTH decision value being added later and silently defaulting to "do not act".
    [TestCase(BlockingWindowPolicy.Decision.Leave, false)]
    [TestCase(BlockingWindowPolicy.Decision.LeaveUnderKeepDialogs, false)]
    [TestCase(BlockingWindowPolicy.Decision.AcceptWithGeneratedName, true)]
    [TestCase(BlockingWindowPolicy.Decision.ForceClose, true)]
    public void ActsOnWindow_CoversEveryDecision(BlockingWindowPolicy.Decision decision, bool expected)
    {
        Assert.That(BlockingWindowPolicy.ActsOnWindow(decision), Is.EqualTo(expected));
    }

    [Test]
    public void ActsOnWindow_TableIsExhaustive()
    {
        // If this fails, a Decision was added and the table above no longer covers the enum.
        Assert.That(System.Enum.GetValues(typeof(BlockingWindowPolicy.Decision)).Length, Is.EqualTo(4));
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
