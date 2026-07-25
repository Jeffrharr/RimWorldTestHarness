# RimWorldTestHarness — design notes

## Problem

Verifying a mod's in-game behavior (does this shadow lean the right way at this latitude on this
day; does this twilight color look right) currently means the developer launches RimWorld, uses
dev-mode tools to set up the scenario by hand, and eyeballs the result — every time, for every
edge case, on every change. That doesn't scale past a couple of manual spot-checks, and it means
regressions can only be caught by a human who remembers to go re-check.

## Approach

Two complementary verification modes, driven by one JSON `ScenarioSpec` format
(`Shared/ScenarioSpec.cs`):

1. **Spec-driven pass/fail.** A scenario lists `Probe` steps: read some named numeric quantity
   (via `Mod/Probes/IProbe`) and compare it against an expected value within a tolerance
   (`Shared/ReportComparer.cs`). A scenario passes only if every probe check passes **and the run
   recorded no errors** (`ReportComparer.AllPass(checks, errors)`). Errors are part of the gate
   because a probe-only gate is vacuous over an empty check list: a scenario with no `Probe` step —
   a pure screenshot or timelapse run — would otherwise report `Pass` no matter how many of its
   steps failed, and a step that silently didn't happen means the run verified less than it claims.
   This is the automatable gate — the piece that can run unattended in a loop and produce a clean
   yes/no.

2. **Screenshot visual-confirm.** A scenario can also list `Screenshot` steps, which just capture
   an image to the report folder. These never affect pass/fail — they're for the cases a number
   can't easily capture (does this twilight color actually *look* warm and long, not just measure
   as such) and are meant to be reviewed by a human or by Claude afterward.

3. **Before/after A/B via feature toggles.** A visual effect is hard to judge from a single frame —
   "is this warm?" only means something next to the same scene *without* the effect. A `SetFeature`
   step flips a named runtime flag in the mod under test (through `Mod/Features/FeatureRegistry`,
   the same one-directional bridge as `IProbe`/`ProbeRegistry` — the harness never references the
   mod), so one scenario in a single game boot can screenshot the effect off, flip it on, and
   screenshot again. The two frames come from an identical world/time, so the only difference is the
   effect. The same flags are what the mod's real settings screen drives for users, so the toggle
   seam isn't test-only scaffolding. See CelestialLighting's
   `Tests/Scenarios/civil_twilight_dusk.json` for a worked example — mod-specific scenarios live in
   the mod's own repo (they name that mod's probes/features); only this harness's generic,
   modset-agnostic demo scenarios (e.g. `daycycle_timelapse`) live here.

All modes share one spec format and one report format (`Shared/ScenarioReport.cs`) so a single
scenario run can produce all of them at once. A run can execute one scenario or a whole **suite** of
them inside a single game load — see "Batching scenarios into one load" for how they are kept isolated
from each other.

## Why a separate project, not bolted onto CelestialLighting

This harness is infrastructure for testing *any* mod in this repo, not specific to one. It follows
the `HarmonyConflictChecker` shape: a dependency-free `Shared` project (pure spec/report/comparer
logic, unit-testable under plain `net8.0`) plus a thin `Mod` adapter (`net481`, the actual
in-game driver) that depends on `Shared` but isn't itself tested directly — the pure logic is
tested, the live-game plumbing is validated by actually running it.

## Architecture

- **`Shared/`** (`netstandard2.0`, no `UnityEngine`/`Verse` dependency) — `ScenarioSpec`/
  `ScenarioStep`/`StepArgs` (the spec format), `ScenarioReport`/`ProbeCheckResult` (the report
  format), `ReportComparer` (pure pass/fail logic), `ScenarioSpecLoader` (JSON parsing), plus the
  whole suite layer — `SuiteList` (selection), `ScenarioResidue` (what a scenario leaves behind),
  `SuitePlan`/`SuitePlanner` (what has to happen between two scenarios), `SuiteScreenshots` (filename
  policy) and `SuiteReport`. That the isolation *decision* is pure is deliberate: it is the part of
  batching that is easy to get subtly wrong and impossible to check by looking at a green run. This is
  the part with real edge-case unit tests (`Tests/RimWorldTestHarness.Tests/`), following the same
  pure-core pattern as `CelestialLighting/Source/Formulas.cs`.
- **`Mod/`** (`net481`) — `HarnessMod` (bootstrap, opt-in via the `RWTH_SCENARIO`/`RWTH_SUITE` env
  vars so the mod is inert in normal play), `ScenarioDriver` (a tick-driven state machine that
  executes a suite's steps against live game state, advanced by `Patch_DriveScenario`'s postfix on
  `Root_Play.Update()` — see "Save loading" below for why it has to be tick-driven rather than
  running synchronously at startup), `FixtureReloader`/`WorldStateReset` (the two Verse-touching
  halves of scenario isolation, applying what `SuitePlanner` decided),
  `Patch_ForceDevMode`/`Patch_ForcedLatitude` (narrow Harmony
  postfixes the driver's `SetTile` step and the vanilla autostart-save check depend on),
  `Probes/IProbe` + `ProbeRegistry` (the extension point other mods use to expose numeric probes),
  `Features/FeatureRegistry` (the parallel extension point for `SetFeature` — mods register named
  `Action<bool>` flag setters so scenarios can toggle effects for before/after screenshots).
- **`Runner/run_test.sh`** — the external driver: launches RimWorld with a scenario or a suite, waits
  for completion, gates on the report. Modeled on `MissileGirl/TestMods/run_test.sh`'s proven
  launch/retry/log-wait/cleanup shape, adapted for a lighter run and (unlike that harness) real GPU
  rendering, since screenshots need it. Refuses to start if another run or a RimWorld session is
  already live, and scopes its kills and temp files to itself — see "A run defends itself instead of
  assuming it's alone" below.
- **`Scenarios/`** — example/reusable `ScenarioSpec` JSON files, plus `*.txt` suite lists.
- **`Fixtures/`** — save files scenarios load from. Not auto-creatable (no headless "new colony"
  API — see below); each one is made manually once. Gitignored.

## Live companion channel (interactive mode)

Alongside the batch mode above, the mod has a second, **interactive** mode: a companion channel that
lets an external client (the sibling `RimWorldDevMCP` server, or its `rwdev` CLI) drive dev-actions
against a game that's *already running* — set the time, read a probe, grab a screenshot — one command
at a time, with results returned live. Batch answers "did this scenario pass?"; the companion answers
"let me poke at whatever's on screen right now."

- **Opt-in, not env-var-gated.** Batch keys off `RWTH_SCENARIO`, which a normally-launched game never
  sets. The companion is instead armed by a mod-settings checkbox (`HarnessSettings` /
  `HarnessMain`), so it works against a game started normally through Steam. Off by default; the mod
  stays inert until it's ticked.
- **Same tick pump, mutually exclusive.** `LiveCommandDriver.Tick()` is pumped from the same
  `Patch_DriveScenario` postfix as `ScenarioDriver`. It bails immediately when a batch scenario is
  `Active`, and no-ops (near-zero cost) when the setting is off — the two never run together.
- **Minimally invasive.** Unlike batch, the companion runs against the user's real game, so it never
  forces DevMode (`Patch_ForceDevMode` keys on `HarnessRuntime.ForceDevMode`, which only batch sets)
  and never touches time speed except for an explicit `FastForward` (which restores the prior speed
  when done). Read actions (probe/screenshot/status) are always safe mid-play.
- **Shared executor.** Both drivers run the identical per-action game logic via `StepExecutor` (with
  `HarnessRuntime` holding the flags the patches read), so batch and live can't drift. The batch
  driver folds each `StepOutcome` into a `ScenarioReport`; the live driver folds it into a
  `LiveResponse`.
- **File-queue transport.** Every game-state mutation must happen on the main tick thread, so an
  in-process HTTP listener would have to marshal onto the tick loop anyway. Instead the driver drains
  a request/response file queue under `$XDG_CACHE_HOME/rimworld-dev-mcp/live` each frame, writing
  atomically (tmp + `File.Replace`). The wire contract is `Shared/LiveProtocol.cs` — pure DTOs with
  no transport assumptions, so a later HTTP transport reuses the exact same parsing. See
  `RimWorldDevMCP/DESIGN.md` for the client half.

### Actions live as real dev commands; the catalog is discovered, not hardcoded

Which dev-actions and probes exist depends on the loaded modset, so the companion **discovers** them
rather than shipping a static list. On map load it emits a `catalog.json` built from three sources:
this harness's own verbs, the registered `ProbeRegistry`/`FeatureRegistry` entries, and — crucially —
RimWorld's **own** dev-action registry. `DevActionCatalog` reflects over `Verse.GenTypes.AllTypes` for
`[LudeonTK.DebugAction]`-attributed methods (the same set the game's dev menu shows), so the catalog
mirrors the actual dev menu for whatever mods are loaded.

New harness actions are added as real `[DebugAction]`s too, not bespoke code: `HarnessDebugActions`
registers the screenshot action under a `RimWorldTestHarness` dev-menu category, sharing one capture
core with the live channel. Honest limits: only zero-arg `DebugActionType.Action` entries are
invokable headlessly — tool-type actions (which need a click target) are listed but not yet callable,
and some `Action` entries open a UI dialog rather than doing something headless. The API-compat tests
pin the `LudeonTK` dev-action surface (`DebugActionAttribute`/`DebugActionType`) and the catalog's
data sources, since `LudeonTK` is where RimWorld relocated its debug tooling and is prone to the
silent-breakage pitfall the parent `CLAUDE.md` calls out.

## Step registration

Steps are a registered extension point, not a hardcoded switch. A step declares itself in two files
and both registries find it by reflection over loaded mod assemblies at startup — so a contributor
(or a mod under test) adds a step without editing anything of ours. See CONTRIBUTING.md for the
how-to; this is the why.

Before this, a new step had to be threaded through five to eight central files: `StepArgs`,
`StepValidator`'s `KnownStepTypes`, `ScenarioResidueAnalyzer`'s switch, `StepExecutor`'s `Dispatch`,
`LiveCommandDriver`'s `HarnessVerbs`, sometimes `WorldStateReset`, plus tests and the README table.
Every contributor's PR edited the same lines as every other's, and forgetting one file failed in a
different way each time. `StartCondition` proved the hazard: it shipped (fd40268) without a case in
the residue switch, silently fell to the `default: ScenarioResidue.All` arm, and reported that it had
dirtied the clock, latitude, camera and map — none of which it touches. Safe, because `All` still
forced a reload, but wrong in a way nothing would ever have surfaced.

### Why the definition is split across two assemblies

A step has a pure half and an impure one, and `Shared/` exists precisely to hold the pure half
without a game:

- `IStepSpec` (Shared) — type name, residue, live-callability, offline arg validation. Consumed by
  `ScenarioSpecLoader`, `StepValidator`, `ScenarioResidueAnalyzer` and `SuitePlanner`.
- `IStepAction` (Mod) — `Execute` against a live `Map`.

Merging them would drag `Verse` into the assembly whose whole purpose is not needing it, and the
suite-isolation decision would then only be checkable by booting RimWorld — the exact cost this repo
exists to cut. The price is that half of a step can go missing, so `StepDiscovery.VerifyHalvesMatch`
checks the pairing at startup and logs which half is absent, because "validates but won't execute" is
otherwise painful to diagnose from a report.

Composites are the one legitimate spec-without-action: a step that also implements `IStepExpander`
desugars at load and never reaches the executor. `Timelapse` is the built-in case.

### Why reflection rather than a Register() call

An explicit registration list is just a central file with extra steps — something to edit and
something to forget. Reflection over `LoadedModManager.RunningMods` means a step works by existing.
There is precedent: `DevActionCatalog` already reflects over `GenTypes.AllTypes` for RimWorld's own
`[DebugAction]` methods, and the standing TODOs on `ProbeRegistry`/`FeatureRegistry` ask for the same
treatment.

Shared's registry additionally self-discovers its own assembly from a static constructor, so offline
consumers — unit tests, any future spec-linting mode — get the built-in vocabulary with no game and
no bootstrap. Without that, every step in every scenario would read as an unknown type under test.

### What stayed closed, and why

`ScenarioResidue` is still a closed enum. A step picks from the existing flags, and adding a genuinely
new kind of world state means adding a flag — the one place a contributor touches shared code. That
is deliberate: the suite planner has to *understand* a residue kind to decide whether `WorldStateReset`
can undo it or the save must be reloaded. A free-form residue string would let a typo read as "leaves
nothing behind", and under-reported residue is the single failure mode that silently lets one scenario
contaminate the next. `SoftResettable`/`RequiresReload` are derived from each other rather than listed
twice, and a test names the reload-only flags explicitly so adding one is never accidental.

Live-callability moved from a hand-kept `HashSet` to a per-step declaration defaulting to false. The
exclusions were always safety decisions rather than gaps — the companion channel points at a real
player's colony, and `PlaceThings` spawns with `WipeMode.Vanish` — and a default of "no" means a new
step cannot become live-callable by being added to one list and forgotten in another.

## Where probe tests live

`Mod/Probes/IProbe.cs`/`ProbeRegistry.cs` is the *only* thing this repo owns for probes — the
interface and the registry. A target mod's actual `IProbe` implementation, and any unit tests for
the logic it wraps, belong in **that mod's own repo**, not here. For the reference case,
CelestialLighting's shadow-lean probe lives at
`CelestialLighting/Source/Probes/ShadowLeanProbe.cs` — a one-line delegation to the already-pure,
already-tested `LatitudeEffect.ForMap(map).Lean` (`CelestialLighting/Source/Formulas.cs` +
`CelestialLighting/Tests/CelestialLighting.Tests/FormulasTests.cs` already cover the actual math,
so the probe itself gets no separate unit test — same pure-core-plus-thin-adapter split
`Formulas.cs`/`FormulasTests.cs` already uses). This repo's own `Tests/` project stays scoped to
what it actually owns (the spec/report/comparer format) and should never accumulate another mod's
domain-logic tests.

Corollary: a target mod's **shipped** DLL should not take a hard reference to
`RimWorldTestHarness.Mod` just to implement `IProbe` — that would make a dev-only test tool a
runtime dependency of a real mod players install. `CelestialLighting/Source/CelestialLighting.csproj`
excludes `Probes/**/*.cs` from its own compile glob (`<Compile Remove>`); a separate
`CelestialLighting/TestMod/CelestialLighting.Probes.csproj` (packageId
`joof.celestiallighting.probes`, hard `modDependencies` on both CelestialLighting and this
harness) links that same file back in and registers it via `ProbeRegistry.Register` in a
`[StaticConstructorOnStartup]` — mirroring `MissileGirl/TestMods/`'s purpose-built-test-mod
pattern rather than adding the reference to the main mod project.

## Save loading: the vanilla autostart mechanism

`Verse.QuickStarter` (`-quicktest`) only calls `SceneManager.LoadScene("Play")` — confirmed by
decompiling `Assembly-CSharp.dll`. It does **not** set up a game or load a save, so it's a dead
end for this harness's purposes.

What actually works is RimWorld's own **autostart save mechanism**, already built into
`Verse.Root_Entry.Start()`: if `Prefs.DevMode` is true and a save file named exactly `autostart`
(case-insensitive) exists under `GenFilePaths.SavedGamesFolderPath`
(`SaveGameFilesUtility.GetAutostartSaveFile()`), vanilla code loads it itself
(`GameDataSaveLoader.LoadGame(FileInfo)`) before any of our code runs. No custom loader is needed
— `Patch_ForceDevMode.cs` (a Harmony postfix scoped to `ScenarioDriver.Active`, so it never
touches the user's real `Prefs.xml`) makes `Prefs.DevMode` read `true` while a scenario is active,
and `Runner/run_test.sh` copies the fixture save to `Saves/autostart.rws` before launch.

This is also *why* `ScenarioDriver` has to be a tick-driven state machine rather than running
synchronously: `HarnessMod`'s `[StaticConstructorOnStartup]` fires before any scene exists, well
before `Root_Entry.Start()` even runs its autostart check, let alone before a map is loaded. So
`ScenarioDriver.Begin()` only sets `Active = true` and stashes the spec; actual step execution
waits for `Patch_DriveScenario`'s postfix on `Root_Play.Update()` before advancing past
`State.WaitingForMap`.

### Readiness is `ProgramState.Playing`, not a non-null map

The obvious gate — wait for `Find.CurrentMap != null` — is wrong, and wrong in a way that produces a
*passing* run. `Game.InitNewGame` sets `ProgramState.MapInitializing`, generates the map (so
`Find.CurrentMap` becomes non-null), and only afterwards calls `FinalizeInit`, which sets
`ProgramState.Playing`. On the `-quicktest` path `Root_Play.Update` therefore pumps the driver during
that window: steps ran against a half-built map and produced a storm of `ArgumentOutOfRangeException`
out of `Verse.TickList.Tick`, the map never rendered, and the captured frame was menu art with the
play HUD over it — yet the report said `Pass: true`, because RimWorld's own logged exceptions are not
step failures.

The autostart/fixture path never hit this, but only by luck: `Game.LoadGame` runs inside a
`LongEvent`, so the pre-existing `LongEventHandler.ShouldWaitForEvent` guard happened to cover the
same window. `ScenarioDriver.ReadyToRun` now waits for `ProgramState.Playing`, which both
`InitNewGame` and `LoadGame` reach through `FinalizeInit`, plus a few settle frames. `LiveCommandDriver`
carries the same check, since it can be pumped during a load too.

The lesson generalises past this bug: "the run exited 0" is not evidence a scenario verified anything.
That is why `Errors` count toward `Pass`, and why a scenario that produces images should always have
its images looked at.

## Batching scenarios into one load

A boot costs 2–4 minutes on the dev machine; a step costs milliseconds. So N scenarios used to cost
N boots, which was the single biggest thing making the harness slow to iterate with. A run now takes
a **suite** — several scenarios executed inside one game load — while scenarios stay authored
independently, one JSON file each, with no cross-references.

Selection is a **suite list file**: one scenario path per line, `#` for whole-line comments, relative
paths resolved against the list file (`Shared/SuiteList.cs`). `Runner/run_test.sh` generates one from
its CLI args (`run_test.sh a.json b.json c.json`, or a shell glob) and also accepts a hand-authored
one (`--suite Scenarios/demo_suite.txt`). Either way the resolved list lands beside the report in
`Runner/reports/`, so a run's artifacts record exactly which scenarios it was asked to cover — a
suite whose membership can't be reconstructed afterwards is a suite whose green result means less
than it looks like. A plain newline-delimited file was chosen over a delimited env var (paths contain
`:` and spaces) and over a JSON suite format (a list of paths does not need a schema).

All scenarios in one run must declare the same `saveFile`, because the save is installed once at boot
and reloaded from mid-run; mixed fixtures are rejected rather than silently run against whichever came
first. `requiredMods` are unioned.

### State isolation: reload the save, but only where a reset can't reach

Scenarios are not independent at runtime. Run back-to-back they leak `HarnessRuntime.ForcedLatitude`,
the game clock, `FeatureRegistry` flags, `TimeSpeed`, camera position, vanilla's `screenshotMode`
flag — and, least reversibly, **map mutations from scene setup**: `PlaceThings` spawns with
`WipeMode.Vanish` (destroying whatever it replaced), `SetTerrain` repaints ground, and both lift fog.

The four options were: declare it and order carefully; reset what we know we changed; reload the save
between scenarios; group by compatibility. We do the **last two together**, and the split is exactly
the flag set in `Shared/ScenarioResidue.cs`:

- Everything except `Map` is **soft-resettable** — `Mod/WorldStateReset.cs` records the pristine
  post-load values and assigns them back.
- `Map` is not restorable at all, so a scenario following one that mutated the map gets a
  **mid-session save reload** (`Mod/FixtureReloader.cs` → `GameDataSaveLoader.LoadGame(name)`).

Option 1 alone was rejected outright: its failure mode is a scenario that passes alone and fails in a
suite, or worse passes in a suite for the wrong reason, which is precisely the plausible-but-wrong
outcome this repo exists to catch. Option 2 alone cannot honestly claim isolation for anything doing
scene setup — and scene setup is how visual scenarios are made legible, so that is the growing case,
not the exotic one.

Cost is what makes the reload the right default rather than a luxury: a reload is seconds against a
boot's minutes, so a suite of five map-mutating scenarios costs one boot plus four reloads instead of
five boots. `Runner/run_test.sh --isolation=` overrides the policy: `auto` (the default, above),
`always` (reload before every scenario — the answer to "I don't trust the residue analysis" while
bisecting), `never` (soft reset only, an explicit assertion that the suite tolerates a shared world).

Scenario **order is never changed**. Reordering to group map-mutating scenarios together would cut
reloads, but then the suite that ran isn't the suite that was written down, and a scenario's result
would depend on which others happened to be in the run. An extra reload is far cheaper than that
ambiguity.

Two deliberate asymmetries in how a shortfall is reported, both about consent:

- `isolation=never` over a map-mutating boundary is a **note** — informational, never failing. The
  caller chose it.
- `isolation=auto` over the same boundary with no save to reload (the `-quicktest` path generates its
  colony at boot and writes none) is an **error**, which fails the suite. Nobody asked for the
  degradation; the environment imposed it, and the fix is a fixture or a split run.

A step type nobody classified is assumed to dirty **everything**, so a future step added to
`StepExecutor` and forgotten in `ScenarioResidueAnalyzer` degrades into "reload between everything"
(slow but correct) rather than "share the world" (fast and wrong). A unit test fails when a valid step
type falls into that default.

### The reload re-arms the readiness gate

`GameDataSaveLoader.LoadGame(name)` is exactly what vanilla's own in-game Load Game does: it *queues*
a long event that clears maps/world, installs a fresh `Game` with `InitData.gameToLoad` set, and
reloads the `Play` scene, where `Root_Play.Start()` picks `gameToLoad` up. `Root.checkedAutostartSaveFile`
is already true by then, so the reloaded scene does not re-trigger the autostart path — which is why
the save name is passed explicitly. Our Harmony patch is on `Root_Play.Update` as a *method* and this
assembly's statics outlive the scene, so the driver keeps being pumped across the reload with its
state intact.

The `ProgramState.Playing` gate above has to be re-armed per load, and on its own it is **not enough**.
Because `LoadGame` only queues, there is a frame or two where `ShouldWaitForEvent` is still false
(there is no `currentEvent` yet and the queued one isn't the standard-window kind), `ProgramState` is
still `Playing`, and `CurrentMap` is still the old map — indistinguishable from "the reload already
finished". `ScenarioDriver.ReloadFinished` therefore also requires `Current.Game` to be a *different
instance*, which is the postcondition that says the reload genuinely happened rather than that we
never left. Vanilla replaces `Current.Game` three times over one load, so the identity always changes.

Two further hazards the reload path introduced:

- `Root_Play.Start()` ends with `ScreenFader` fading in from black over 0.5s, so the initial-load
  settle of 5 frames would let a `Screenshot` capture a partly-black frame. Post-*reload* settle is 60
  frames instead (≥ 0.5s at 60fps; a lower framerate only makes the wait longer in wall-clock terms).
- A reload that never completes must fail loudly, not hang. The driver holds a wall-clock deadline and
  aborts the suite with a named error if it expires, and `FixtureReloader` pre-checks the save exists
  (`GenFilePaths.FilePathForSavedGame`) — without that, a missing save throws on the long-event thread
  and all the caller sees is a timeout.

The reload is also a real `[DebugAction]` ("Quickload autostart save"), same rule as
`HarnessDebugActions`' screenshot: harness capabilities are game dev commands sharing one core, not a
parallel private path. It also lets a human confirm a mid-session reload works with one click in a
normally-launched game, without running a batch.

### Suite report: a wrapper, not a widened scenario report

`Shared/ScenarioReport.cs` is unchanged. `SuiteReport` wraps a list of them plus suite-level `Errors`
and `IsolationNotes`, and `SuiteReportSerializer` writes the **bare** single-scenario shape for a run
launched with `RWTH_SCENARIO` and the wrapper for one launched with `RWTH_SUITE`. Keying off the launch
mode rather than the scenario count means the runner always knows which shape to expect; a consumer
reading a report cold tells them apart by the `Scenarios` key. The single-scenario path — the fallback
— therefore produces byte-identical output to before.

Three properties the suite gate (`ReportComparer.AllPass(SuiteReport)`) exists to hold, each a version
of the vacuous-truth bug that `AllPass(checks, errors)` already guards against one level down:

- **No suite-level errors.** A reload that never completed, an unparsable suite list or a screenshot
  name collision invalidates the run as a whole, even if every scenario that did run passed.
- **At least one scenario.** `All()` over an empty list is `true`, so an empty suite would otherwise
  pass.
- **Every scenario passed** — including ones a mid-suite abort never reached. Those are still listed,
  carrying an explicit "did not run" error, so a truncated suite can neither shrink into a green one
  nor look like scenarios that ran and failed. A scenario file that fails to load likewise becomes a
  named, step-less placeholder rather than vanishing from the list.

A failing *step* never truncates anything: it is recorded against its scenario and the run continues,
same as before.

### Screenshot names are qualified, and checked anyway

Two scenarios written independently will happily both ask for `shot.png`, or for a `Timelapse` with
prefix `timelapse`, and in one shared report folder the second silently overwrites the first — a green
run over the wrong images. Two independent defences: suite screenshots are prefixed with their
scenario (`<sanitized name>__<fileName>`, `Shared/SuiteScreenshots.cs`), **and** the final names are
checked for duplicates at plan time regardless, which catches what qualification can't (two names that
sanitize alike, or one scenario reusing a fileName). Single runs are left unqualified so their output
filenames are exactly what they have always been.

`Runner/run_test.sh` mirrors the qualification rule in bash to find a suite's timelapse frames for
ffmpeg. That duplication is deliberate and bounded: the rule is trivial by design (sanitize to
`[A-Za-z0-9._-]`, join with `__`), a unit test pins the exact C# spelling, and a drift would surface as
the existing loud "declares a timelapse but no frames were written" warning rather than a wrong video.

## Screenshot capture requires GPU rendering

`ScreenCapture.CaptureScreenshot` needs the game actually rendering a frame, which rules out a
pure headless/`-batchmode -nographics` launch (the mode `MissileGirl/TestMods/run_test.sh` uses,
since it only needs Player.log output). `Runner/run_test.sh` launches normally instead.

## A run defends itself instead of assuming it's alone

`Runner/run_test.sh` mutates global machine state — the one `RimWorld/Mods` folder, the one save-data
dir, the one running game. Originally it assumed exclusivity: fixed `/tmp` backup names, and
`pkill -9 -x RimWorldLinux` to guarantee teardown. Both broke in practice, and not because of
parallelism: a run failed confusingly once when a *just-exited* session's shutdown writes landed
mid-run. Two runs sharing one backup file is worse still — the loser restores the winner's snapshot
over the user's real `ModsConfig.xml`.

The fix is scoping, not sandboxing. This is a Steam Deck (~4GiB free, one integrated APU); two
rendering RimWorlds is not a goal, so a run is allowed to *refuse* rather than isolate:

- **Termination is PID-scoped**, TERM then KILL. Name-based killing cannot distinguish our game from
  the user's, and the user's must win. The escalation is what preserves the old `pkill -9` guarantee
  that a hung game still gets reaped.
- **A run guard** — an exclusive `flock`, plus a refusal to start while any `RimWorldLinux` lives —
  makes overlap impossible instead of merely survivable. Cheaper and more honest than isolating state
  the machine can't afford to run twice anyway.
- **Per-run scratch dir** for backups and logs. `Player.log` had to move there too: the runner greps it
  for `RWTH: harness loaded` / `RWTH: scenario complete`, so sharing it means reading another run's
  markers as your own.

Save-data isolation is the one piece left opt-in (`RWTH_ISOLATE_SAVEDATA=1`). It uses RimWorld's own
`-savedatafolder=` arg — `Verse.GenFilePaths.SaveDataFolderPath` consults
`GenCommandLine.TryGetCommandLineArg("savedatafolder")` before falling back to
`Application.persistentDataPath` — deliberately *not* `$XDG_CONFIG_HOME`, which UnityPlayer.so
demonstrably reads but which we could not prove governs this build's `persistentDataPath`. Depending on
an unproven assumption here is the one failure mode worse than no isolation: a run that appears
isolated while writing to the user's real config. So the mechanism is first-party, and because it logs
`Save data folder overridden to <path>`, the runner asserts the game actually used our directory rather
than hoping. It stays off by default because a fresh save-data root is itself an unvalidated behaviour
change (no `Screenshots/`, empty `Saves/`, only the `Config/` we seed).

Per-run bind mounts of `RimWorld/Mods` (`bwrap` is available) are deliberately not attempted:
`setup_symlink` is idempotent and only removes links it created, which is enough while runs can't
overlap.

## Timelapse: video as a clock sweep, not a screen recording

A `Timelapse` step produces a video of the map over a span of hours. It is the only composite step:
`Shared/TimelapseExpander.cs` desugars it at load time into one `SetTime` → `Wait` → `Screenshot`
triple per frame, and `Runner/run_test.sh` stitches the resulting PNG sequence with ffmpeg after the
run. Nothing new touches the game — `StepExecutor` and `ScenarioDriver` are unchanged, and the whole
feature is unit-tested offline because the interesting part is pure string/number work.

Sweeping the **clock** rather than recording wall-clock time is the substantive choice. The harness
already jumps the clock deterministically (`DebugSetTicksGame`), so a clock-swept sequence is
frame-aligned and reproducible: the same scenario against two builds of a mod under test yields two
videos comparable frame-for-frame. A real-time recording drifts with framerate and can't do that.

It's also the only approach that works here at all. This is a Wayland session, so capturing the game
window from outside needs either `kmsgrab` (elevated privileges) or a PipeWire portal (an
interactive permission prompt) — both hostile to an unattended run, and `x11grab` under XWayland
generally returns black.

Two deliberate semantics, both covered by tests:

- The hour range is **half-open**, `[fromHour, toHour)`, so a `0 → 24` sweep gives 24 distinct
  frames rather than 25 with midnight duplicated at each end, and the video loops seamlessly.
- Unknown arg keys are **rejected**, not ignored. `Args` is case-sensitive, so a `stephours` typo
  would otherwise silently fall back to the default and produce a plausible-but-wrong video — the
  worst failure mode for a verification tool.

A malformed `Timelapse` is left un-expanded rather than dropped: the reason lands in
`ScenarioSpec.LoadErrors` → the report, and `StepExecutor` fails the leftover step, so a run can't
come back green having silently skipped an entire sweep.

`settleFrames` (default 2) exists because a clock jump doesn't necessarily update the glow grid and
shadow direction within the same frame, and a capture taken too eagerly would record stale lighting.
The default is a reasoned guess, not a measured one — it wants confirming against a real run.

## Screenshots hide the UI by default

`HarnessDebugActions.CaptureScreenshotTo(path, hideUi)` drives vanilla's own screenshot mode
(`Find.UIRoot.screenshotMode.Active`), which suppresses the dev toolbar, colonist bar, main buttons,
alerts, letters, tutor, messages and tooltips. Batch runs force `Prefs.DevMode` on, so without this
every capture carries the dev toolbar over the map — noise in one screenshot, and 48× the noise in a
timelapse. Scenarios opt back in with `"hideUi": "false"` when the UI is what's under test.

Setting the flag in the same frame as the capture is safe because Unity runs `Update` (where the
driver pumps steps) before `OnGUI` and rendering, so the frame grabbed at end-of-frame is already
clean.

It defaults to **off** at the capture core so the two interactive callers are unaffected: the
dev-menu `[DebugAction]` behaves as it always has, and the live channel points at a real player's
running game, where a hidden UI would be a nasty surprise. Only the batch path opts in, and it
doesn't restore the flag inline (the capture finishes over later frames) — `ScenarioDriver.Finish`
clears it at end of run.

## Scene setup: spawned at runtime, not authored into the save

`shadow_lean_equinox` exposed the limit of a purely numeric gate: the probe passed at `1.02e-06`
against a `0.02` tolerance, and the screenshot beside it was useless, because the fixture's flat sand
has almost nothing tall enough to cast a shadow. A day-cycle `Timelapse` over the same ground has the
same problem, 48 times over. `PlaceThings` / `SetTerrain` / `LookAt` exist to fix that: a lattice of
pillars on uniform ground, with the camera aimed at it, makes shadow direction and length legible at
a glance.

Save files are plain XML and a building really is a trivially authorable node (`<thing
Class="Building">` with `def`, `id`, `pos`, `stuff`), so hand-authoring the scene into a fixture was
the obvious alternative. Four things decided it the other way:

- **Terrain isn't XML.** `<terrainGrid>` is a `<topGridDeflate>` base64 deflate blob keyed by def
  shortHashes that vary with the loaded modset. At runtime it's one `TerrainGrid.SetTerrain` call.
- **It wouldn't cover the no-fixture path.** `Runner/run_test.sh` falls back to `-quicktest` when the
  scenario names no existing fixture, and a generated colony has no save file to edit — so a
  save-authored scene simply cannot exist there, whereas a runtime-built one works on both paths.
  (`shadow_casters_daycycle` itself names the `minimal_colony.rws` fixture, because a generated
  colony's map centre needs rock and overhead roof cleared before it makes a usable backdrop; see
  `TODO.md`'s quickstart entry.)
- **It fails silently.** A `def` absent from the active modset makes RimWorld drop the node at load
  with only a log warning. `SceneBuilder` instead counts what actually spawned and reports any
  shortfall cell by cell. Note it has to ask `GenSpawn.CanSpawnAt` explicitly to do that: the `Thing`
  overload of `GenSpawn.Spawn` — unlike the `ThingDef` one — never consults it, and returns null only
  for a null map, an out-of-bounds cell or an already-spawned thing. Relying on that null alone would
  have reported a wall standing in deep water as successfully placed.
- **It's version-coupled with no compile signal.** Hand-authored XML breaks invisibly on a save-format
  change; the vanilla API surface is pinned in `Tests/RimWorldTestHarness.ApiTests` instead.

None of that rules the XML route out for the job it *is* right for — generating a reproducible
committed fixture (see `TODO.md`). `SceneLayout` produces plain `(def, stuff, dx, dz, rot)` tuples, so
a save-XML emitter can consume the same plan when that comes up.

Coordinates in a plan are **anchor-relative offsets, never absolute cells**, and the anchor defaults
to map centre. That's what keeps `SceneLayout` pure — resolving "center" needs a live `Map`, so
`SceneBuilder` does it — and it's why one scenario works against both the committed 250×250 fixture
and a `-quicktest` colony.

Scene steps lift fog by default (`unfog`, opt out with `"false"`). RimWorld draws neither terrain nor
things in fogged cells, and a freshly generated colony has only a small revealed pocket — so a scene
built at map centre is *completely invisible* while every step still reports success. Fog is lifted
across the whole map rather than the built footprint, because a shadow falls well outside the cells its
caster occupies, and at a low sun it falls a long way. Nothing is ever saved, so there is no lasting
effect on a colony.

### Clearing the footprint is opt-in, but a roofed footprint is never silent

A generated colony frequently has rock where a scenario wants its pad, which showed up on the
`-quicktest` path as `placed 12 of 16 Wall — 4 refused`. The `clear` arg (`PlaceThings`, `SetTerrain`)
destroys the destroyable things in the step's footprint and strips its roof. Three decisions in it:

- **Roof matters more than placement.** Overhead mountain roof lives in `Map.roofGrid` and *survives*
  the rock beneath it being destroyed, and it darkens the cell. Since these scenes exist to be
  photographed for their lighting, a pad half under mountain is wrong for the exact quantity being
  measured — worse than a few missing pillars, because nothing about it looks broken. So `clear`
  strips roof from every footprint cell, including cells that held nothing. `SetTerrain` clears its
  whole rect for the same reason: shadows fall on the pad, not only on the cells with pillars.
- **Clearing runs before `CanSpawnAt`, which is then re-run rather than skipped.** `CanSpawnAt` fails
  on `!c.Walkable(map)`, so a *mineable* — therefore destroyable — rock wall refuses a placement that
  clearing could have made possible. Re-running the check is what keeps genuinely impossible cells
  (deep water, an indestructible edifice) from turning into silent successes.
- **It defaults to `false`, unlike `unfog`.** Lifting fog changes what is drawn; clearing permanently
  deletes map content. `SceneBuilder`'s cores are also reachable from the "Place shadow-caster grid"
  `[DebugAction]`, which `DevActionCatalog` discovers and the live companion channel can invoke against
  a real player's colony — a default-on clear would be one invoke from bulldozing part of someone's
  base with no undo. The safety argument only works because the *unsafe* direction can't hide: a
  rock-blocked footprint already fails loudly (`PlaceThings` reports every refused cell), so choosing
  the conservative default costs no verification.

That last point leaves one gap, and it's plugged rather than accepted: terrain paints and pillars stand
perfectly well under overhead roof, so a scenario that *forgot* `clear` would produce a wrongly-lit
scene with nothing to show for it. Every scene step therefore counts roofed cells in its footprint
whether or not it was asked to clear them, and `Log.Warning`s when it built into roof without clearing.
A warning, not a step failure — a scenario is allowed to want a roofed scene — but never silence.

`Shared/SceneClearing.cs` holds the policy: which `ThingCategory` values may be destroyed
(`Building`/`Plant`/`Item`/`Filth` — a whitelist, so a category a future RimWorld adds is spared rather
than bulldozed), and three verdicts. `Destroy`, `Leave` (pawns are never destroyed: a colonist can
wander onto the pad, so destroying one would make runs destructive *and* nondeterministic, and a pawn
is passable so leaving it costs the scene nothing), and `Blocked` — a thing that occupies the footprint
and cannot be removed, which **fails the step**. Categories cross the pure/adapter boundary as the
enum's own member *names*, so the adapter does no branching at all and cannot drift from the table;
`ApiCompatibilityTests` pins those names, since a rename would otherwise quietly turn a category into
"leave alone" and clearing would stop clearing while still reporting success.

Two further consequences worth knowing. `PlaceThings` spawns with `WipeMode.Vanish`, which destroys
whatever occupies the footprint; that's acceptable in a batch run (the fixture is restored by the
runner and the game is never saved) and is why these three verbs are absent from
`LiveCommandDriver`'s `HarnessVerbs`.

Note that keeping them out of `HarnessVerbs` is **not** the same as making them unreachable live, and
an earlier version of this document wrongly implied it was. `LiveCommandDriver` treats any
unrecognised action name as a native dev-action and falls through to `DevActionCatalog.Invoke`, so the
`[DebugAction]` wrapper is invocable over the live channel against a real player's colony. That is the
reason `clear` defaults to *false* while `unfog` defaults to true: unfogging a colony is a cosmetic
annoyance, whereas a default-on bulldoze would be one live-channel call from destroying someone's
base with no undo. Anything added to that dev action inherits the same exposure.

And the default `grid` layout is separate pillars rather than a closed wall rectangle, both because
each pillar throws its own readable shadow and because a rectangle would read as a room.

## API compatibility tests

`Tests/RimWorldTestHarness.ApiTests/` (Mono.Cecil, pattern:
`PerformanceSearch/Tests/SearchFix.Tests/`) pins every vanilla member `Mod/` depends on —
`Prefs.DevMode`, `WorldGrid.LongLatOf(PlanetTile)`, `Root_Play.Update`, `GenDate`'s tick constants
and `DayOfYear`/`HourFloat`/`LocalTicksOffsetFromLongitude`, `TickManager`'s tick/speed members,
`LongEventHandler.ShouldWaitForEvent`, `ScreenCapture.CaptureScreenshot` — plus the vanilla
autostart-save chain (`Root_Entry.Start`, `SaveGameFilesUtility.GetAutostartSaveFile`,
`GameDataSaveLoader.LoadGame(FileInfo)`) that "Save loading" above depends on even though nothing
in `Mod/` calls it directly. `ScreenCapture` lives in a separate `UnityEngine.ScreenCaptureModule.dll`
alongside `Assembly-CSharp.dll`, not inside it, so this project loads two Mono.Cecil
`ModuleDefinition`s. A separate project from `Tests/RimWorldTestHarness.Tests/` (rather than one
project like `PerformanceSearch` uses) because these tests are `[Category("RequiresGameDll")]` and
`Assert.Ignore` themselves out when the real DLLs aren't present; `test.sh` runs both projects.
