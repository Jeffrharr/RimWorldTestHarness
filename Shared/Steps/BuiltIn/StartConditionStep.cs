using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Starts a vanilla GameCondition (solar flare, eclipse, ...) so scenarios can exercise
// condition-driven effects no clock or latitude jump can produce.
//
// Residue is GameConditions — and declaring it here fixes a real bug. StartCondition was added
// (commit fd40268) without a case in ScenarioResidueAnalyzer's switch, so it fell to the
// `default: ScenarioResidue.All` arm. That was SAFE (All includes non-soft-resettable state, so a
// suite still reloaded between scenarios) but silently wrong in its reporting: a suite's isolation
// notes claimed a StartCondition scenario had dirtied the clock, latitude, camera and map, none of
// which it touches. Exactly the failure mode this registry exists to make impossible — residue is
// now declared next to the step instead of in a switch someone can forget.
//
// Not live-callable: registering a condition on a real player's colony is a visible, lasting change
// to their game, which is the line the companion channel does not cross.
public sealed class StartConditionStep : IStepSpec
{
    public string Type => StepArgs.StartConditionType;
    public ScenarioResidue Residue => ScenarioResidue.GameConditions;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;
        return true;
    }
}
