# Contributing

The most common contribution is a **new step type** — a verb scenarios can use. That's designed to
be a two-file change that touches nothing existing, so start there.

## Setup

```bash
./build.sh    # builds Mod/ (net481) -> 1.6/Assemblies/
./test.sh     # offline unit tests + API-compatibility tests
```

You don't need a mod under test. The harness runs standalone — its own scenarios in `Scenarios/` use
only vanilla defs:

```bash
Runner/run_test.sh Scenarios/daycycle_timelapse.json
```

You do need a fixture save (`Fixtures/README.md`) for a scenario that names one; without it the
runner falls back to `-quicktest` and generates a colony at boot.

## Adding a step

A step has two halves, in two assemblies, because `Shared/` is `netstandard2.0` with no
`Verse`/`UnityEngine` reference — that's what keeps validation and suite-isolation logic testable
without booting RimWorld.

**1. The pure half** — `Shared/Steps/BuiltIn/YourStep.cs`, implementing `IStepSpec`:

```csharp
public sealed class SetWeatherStep : IStepSpec
{
    // Arg names live on the step that owns them. Don't touch Shared/StepArgs.cs — that holds the
    // built-in vocabulary that predates the registry.
    public const string StepType = "SetWeather";
    public const string WeatherDefArg = "weatherDef";

    public string Type => StepType;
    public ScenarioResidue Residue => ScenarioResidue.Weather;
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(WeatherDefArg, out string? defName) || string.IsNullOrWhiteSpace(defName))
        {
            error = $"'{WeatherDefArg}' is required";
            return false;
        }
        error = null;
        return true;
    }
}
```

**2. The executing half** — `Mod/Steps/BuiltIn/YourAction.cs`, implementing `IStepAction`:

```csharp
public sealed class SetWeatherAction : IStepAction
{
    public string Type => SetWeatherStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        WeatherDef def = DefDatabase<WeatherDef>.GetNamedSilentFail(args[SetWeatherStep.WeatherDefArg]);
        if (def == null)
            return StepOutcome.Fail($"No WeatherDef named '{args[SetWeatherStep.WeatherDefArg]}'");

        ctx.Map.weatherManager.TransitionTo(def);
        return new StepOutcome { WaitFrames = StepHelpers.SceneSettleFrames };
    }
}
```

That's the whole step. Both registries discover implementations by reflection over loaded mod
assemblies at startup, so **no switch, list, or registration call needs editing** — including from a
third-party mod's own assembly.

`SetWeather` is the worked example and is deliberately small enough to read end to end:
`Shared/Steps/BuiltIn/SetWeatherStep.cs` + `Mod/Steps/BuiltIn/SetWeatherAction.cs`.

### Getting the four properties right

| Property | Get it wrong and… |
|---|---|
| `Type` | must be unique. A collision is reported at startup and the first registration wins. |
| `Residue` | **the dangerous one.** Under-report and a suite runs the next scenario against a world your step dirtied, while believing it was isolated. |
| `LiveCallable` | `true` lets the interactive channel run your step against a **real player's colony**. Default to `false`. |
| `TryValidate` | must be pure — no filesystem, no game state. It runs offline at load, where a failure costs seconds instead of a whole game boot. |

**On residue:** pick from the existing `ScenarioResidue` flags where you can. If your step dirties a
genuinely new kind of state, add a flag to the enum — that is the one case where you'll touch a
shared file, and it's deliberate: the suite planner has to *understand* a residue kind to decide
whether it can be undone. A free-form residue string would let a typo silently read as "leaves
nothing behind," which is the one direction that quietly breaks isolation. Adding a flag means:

1. Add it to `ScenarioResidue` and to `All`.
2. Add a label in `ScenarioResidueAnalyzer.Describe`.
3. Either add it to `SoftResettable` **and** teach `Mod/WorldStateReset.cs` to restore it, or leave
   it out and accept that suites reload the save after your step. Leaving it out is the safe default.
4. Update `SoftResettable_AndRequiresReload_PartitionAll`, which names the reload-only flags on
   purpose so this is never accidental.

### Composite steps

A step that desugars into other steps at load time (like `Timelapse`) also implements
`IStepExpander`. It gets a spec but no action — `StepDiscovery` knows composites legitimately have no
executing half. Declare the residue its *expansion* would have, so a malformed one can't look cleaner
than a valid one.

## Adding a probe or a feature flag

These are for the mod under test, not the harness, and were already extension points.

- **Probe** — implement `IProbe` (one `float Read(Map)`), register with `ProbeRegistry.Register`.
  Optionally also implement `IProbeMetadata` so the live catalog can describe it.
- **Feature flag** — `FeatureRegistry.Register(name, setter)`, which the `SetFeature` step calls by
  name. This is what makes off/on A/B screenshots in a single boot possible.

Both go in a **dev-only bridge assembly** that references your mod *and* the harness. The dependency
only ever points one way: your shipped mod must never reference `RimWorldTestHarness`, a dev tool.
See the README's "Making your mod testable".

## Testing

- **Pure logic gets offline tests.** Anything in `Shared/` is unit-testable with no game; use it.
  `Tests/RimWorldTestHarness.Tests/StepRegistryTests.cs` is the pattern for a step.
- **New vanilla API dependencies get an API test.** If your step calls something in
  `Assembly-CSharp.dll`, add a `Mono.Cecil` check to `Tests/RimWorldTestHarness.ApiTests`. This is
  not busywork: if Ludeon renames a base method, an `override` of it silently becomes an independent
  method that compiles fine and is never called. The API tests are the only thing that catches that
  before a live run does.
- Re-run `./test.sh` after every RimWorld update.

## Code style

From the parent `CLAUDE.md`:

- **Pure core, thin adapter.** Nontrivial math or branching goes in a dependency-free class taking
  primitives and returning primitives; code touching live RimWorld objects stays a thin wrapper.
- **No `continue`/`break` in loops.** Invert the condition and wrap the body in an `if`, or extract a
  named predicate.
- **Pull branching into small named functions** rather than inlining conditionals deep in a method.
- **Document decisions, not mechanics.** Explain *why* a thing is the way it is; the code already
  says what it does.

These are proxies for one goal — reducing what a reader has to hold in their head. If following one
would add an indirection to chase rather than remove one, don't.

## The invariant behind a lot of odd-looking code

**A green run must never mean less than it looks like.** It explains why:

- the gate fails on *any* error, not just failed probes — a scenario whose every step errored has
  zero probe checks, and "nothing was verified" otherwise reads exactly like "everything passed";
- an invalid step is kept rather than dropped, so it fails again at execution;
- an empty suite fails instead of vacuously passing;
- scenarios a mid-suite abort never reached are listed with an explicit "did not run" error;
- an unrecognised step's residue is assumed to be *everything*.

If you're deciding between failing loudly and degrading quietly, fail loudly.

## Commits and PRs

- Small, focused commits — one logical change each.
- Short subject line, then a body explaining what changed, why, and any non-obvious consequences or
  trade-offs.
- Say what you verified. "336 tests pass, `--print-config` checked with zero/one/two mods" is worth
  more than "works".
- If you found a pre-existing bug while working, calling it out separately is welcome.
