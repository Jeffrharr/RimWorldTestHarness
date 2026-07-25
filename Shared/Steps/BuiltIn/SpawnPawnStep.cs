using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Spawns pawns onto the map — wild animals, player colonists, or hostile-faction raiders. All the
// placement arithmetic and arg validation is in the pure Shared/PawnLayout.cs; the game-touching half
// is Mod/Steps/BuiltIn/SpawnPawnAction.cs.
//
// One step covers every faction because they differ only in the Faction handed to PawnGenerator:
// wild is a null faction, player is Faction.OfPlayer, hostile is a resolved enemy faction. Gender and
// hediffs are optional refinements on top.
public sealed class SpawnPawnStep : IStepSpec
{
    public string Type => StepArgs.SpawnPawnType;

    // A spawned pawn is left on the map, so a following scenario in the same load would find it there:
    // Map residue, exactly like PlaceThings. The suite reloads the fixture before the next scenario.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Not live-callable: the companion channel points at a real player's running colony, and dropping
    // pawns — least of all a hostile raider — into it is the kind of world mutation that channel
    // promises not to do. Scenario runs load a throwaway fixture and never save it.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        PawnLayout.TryPlan(args, out _, out error);
}
