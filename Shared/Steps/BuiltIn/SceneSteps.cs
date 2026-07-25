using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Scene setup: put deliberate, known-position geometry on the map so a visual test has something to
// actually show.
//
// These are the only built-ins with real load-time validation, and the only ones that can be checked
// this thoroughly offline: SceneLayout's planners are pure, so the whole layout can be dry-run
// without a Map. A bad `cols` is reported before the game spends minutes producing frames of an
// empty scene.
//
// None is live-callable, and that is a safety decision rather than an oversight. The companion
// channel points at a real player's running colony, and PlaceThings spawns with WipeMode.Vanish,
// which destroys whatever occupies the footprint. Silently bulldozing part of someone's base over a
// file-drop channel would break the minimally-invasive promise that channel is built on. Scenario
// runs are where these belong — they load a throwaway fixture and never save it.

public sealed class PlaceThingsStep : IStepSpec
{
    public string Type => StepArgs.PlaceThingsType;
    public ScenarioResidue Residue => ScenarioResidue.Map;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        SceneLayout.TryPlan(args, out _, out error);
}

public sealed class SetTerrainStep : IStepSpec
{
    public string Type => StepArgs.SetTerrainType;
    public ScenarioResidue Residue => ScenarioResidue.Map;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        SceneLayout.TryPlanTerrain(args, out _, out error);
}

// Camera only — LookAt moves the view and changes nothing the lighting depends on.
public sealed class LookAtStep : IStepSpec
{
    public string Type => StepArgs.LookAtType;
    public ScenarioResidue Residue => ScenarioResidue.Camera;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        SceneLayout.TryPlanLookAt(args, out _, out error);
}
