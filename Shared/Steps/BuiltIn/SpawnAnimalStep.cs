using System.Collections.Generic;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Spawns wild animals onto the map so a scenario has live pawns to photograph or exercise — the first
// pawn-spawning step. All the placement arithmetic and arg validation is in the pure
// Shared/AnimalLayout.cs; the game-touching half is Mod/Steps/BuiltIn/SpawnAnimalAction.cs.
//
// Scope is deliberately wild animals only for now: an animal generates with no faction, no apparel and
// no gear, so PawnGenerator.GeneratePawn(kind, null) is the whole story. Colonists and faction pawns
// drag in faction assignment, equipment and relations, which this step does not yet handle — the
// action rejects a non-animal kind rather than silently generating a broken pawn.
public sealed class SpawnAnimalStep : IStepSpec
{
    public string Type => StepArgs.SpawnAnimalType;

    // A spawned pawn is left on the map, so a following scenario in the same load would find it there:
    // Map residue, exactly like PlaceThings. The suite reloads the fixture before the next scenario.
    public ScenarioResidue Residue => ScenarioResidue.Map;

    // Not live-callable: the companion channel points at a real player's running colony, and dropping
    // wild animals into it is the kind of world mutation that channel promises not to do. Scenario
    // runs load a throwaway fixture and never save it, which is where a spawn belongs.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error) =>
        AnimalLayout.TryPlan(args, out _, out error);
}
