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
desugars at load and never reaches the executor. `Timelapse` and `TickLapse` are the built-in cases.

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

## Vision asserts: a rubric an LLM can judge

The third verification tier, alongside the numeric probe gate and bare screenshots. A scenario
declares in words what its images should show; the run emits that rubric with the images and the
game's recent warnings/errors attached; a judge answers later.

It exists because the other two tiers can both be green while the thing under test is broken.
CelestialLighting #15 shipped with unit tests AND a numeric probe passing while the effect rendered
nothing at all — only a by-hand off/on comparison caught it. A probe proves a formula returns the
right number. Nothing proved the number reached the screen.

### The harness does not call an LLM

The run emits a packet; a Claude session or the RimWorldDevMCP channel writes a verdict back into the
report. An inline API call was the alternative and was rejected: it would put an API key, a per-run
cost and a network dependency inside the gate, and it would move the pass/fail decision somewhere it
could not be unit-tested. Emitting a packet keeps every decision in `Shared/VisionGate.cs`, which is
pure and offline-tested like the rest of the isolation and comparison logic.

The cost is that a run finishes with rubrics unanswered. That is handled by naming it rather than
hiding it — see below.

### Only a confident FAIL blocks

| Verdict | Outcome |
|---|---|
| none yet | pending — does not block |
| fail, confidence >= gate | **blocked** |
| fail, confidence < gate | needs a human |
| pass, confidence < gate | needs a human |
| pass, confidence >= gate | passed |

The asymmetry is the whole design. An LLM judging a screenshot is a fallible reviewer, and a gate
that red-builds on its uncertain opinion gets switched off within a week — at which point it protects
nothing, which is strictly worse than never having added it. A confident "this is broken", though, is
exactly the signal that a probe-green run was lying, and that is worth failing over.

A confident vision PASS never rescues a run that failed on a probe or an error. The tier can only
subtract confidence, never add it.

### Why a provisional pass is allowed to exist

Everywhere else in this repo, unverified reads as failed: an empty suite fails, a scenario whose
steps all errored fails, an unrecognised step's residue is assumed to be everything. A pending vision
assert breaks that pattern deliberately, because "nobody has judged this yet" is the NORMAL state
immediately after a run — there is no judge in the loop yet by design.

The rule is preserved a different way: the shortfall is stated out loud everywhere the result is.
`VisionGate.Describe` puts it in the report, ScenarioDriver puts it in the Player.log line, and
run_test.sh prints `NOTE: N vision assert(s) awaiting review — this result is provisional`. A silent
provisional pass would be the green-run-means-less failure; a loud one is an honest intermediate
state.

`VisionGate.ReviewComplete` is what a consumer checks to tell "fully gated" from "gated on probes
alone", since the Pass flag alone cannot express the difference.

### Evidence: images plus the log

The judge gets the named screenshots and an excerpt of the game's own warnings and errors, read from
`Verse.Log`'s in-memory buffer rather than Player.log on disk (the file is owned by Unity and written
concurrently; the buffer is already parsed and deduplicated). A missing def or an exception thrown
mid-step is invisible in a screenshot and obvious in the log, and asking a judge to rule on a picture
while withholding the stack trace beside it would be daft.

Video is not sent. An mp4 is not consumable by the judge, so a timelapse is reviewed by naming
whichever frames matter from the PNG sequence the run already keeps.

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
(`GameDataSaveLoader.LoadGame(FileInfo)`) before any of our code runs. No custom loader is needed —
`Runner/run_test.sh` copies the fixture save to `Saves/autostart.rws` before launch and seeds
`<devMode>True</devMode>` into `Prefs.xml`, restoring the real file on teardown.

**The DevMode half cannot be done from inside the game, and for a long time this document claimed
otherwise.** `Patch_ForceDevMode` was described here as what made `Prefs.DevMode` read `true` in time
for the autostart check. It cannot: `Verse.Root.Start()` only *queues*
`PlayDataLoader.LoadAllPlayData()` as an async long event, and both `LoadedModManager.LoadAllActiveMods()`
and `StaticConstructorOnStartupUtility.CallAll()` run inside it — while `Root_Entry.Start()` performs
the autostart check synchronously on the line after `base.Start()` returns. At that moment no mod
assembly is loaded, Harmony has patched nothing, and `HarnessRuntime.ForceDevMode` is still false, so
vanilla reads the user's real pref. Runs only ever worked because that pref happened to be `true`;
when it flipped to `false` the game booted to the main menu and every scenario either timed out or
measured whatever map a human had loaded by hand. Seeding the pref in the runner is what actually
holds. `Patch_ForceDevMode` stays, but only for `Prefs.DevMode` reads later in the boot (see the
alerts/tutor suppression below) — it is not part of the autostart chain.

`ScenarioDriver` still has to be a tick-driven state machine rather than running synchronously,
because the map loads asynchronously and no step can touch it until it exists. So
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

Per-run bind mounts of `RimWorld/Mods` (`bwrap` is available) are deliberately not attempted: claimed
symlinks (below) record and restore exactly what was there, which is enough while runs can't overlap.

## The lock has to cover the assets, not just the run

The run guard above serialises *runs*. It does not, on its own, serialise the thing agents actually
collide over — **which build is installed at the path the game resolves to**.

`RimWorld/Mods/<Mod>` is a permanent symlink to a mod's main checkout, and the runner will not
repoint it at a git worktree just because one was named (two folders sharing a `packageId` is its own
problem). So live-testing a branch meant copying the branch's `1.6/Assemblies` over the main
checkout's, running, and copying back — all of it *outside* the lock. Three failures came out of that,
and each one produced a confident wrong answer rather than an error:

1. **A run measuring someone else's build.** Agent A installs its DLL, then blocks on the lock B
   holds; B's game boots against A's assembly. B reported a full A/B frame set as proof of a fix that
   was never loaded. Checking the lock is free before installing does not help — the other run can
   take it in the seconds between the check and the launch.
2. **A crashed run's leftovers inherited silently.** Teardown never ran, so the branch DLL and a
   `Mods/TestMod` pointing at a since-merged worktree survived into the next run. Every worktree's
   probe bridge shares one basename and `packageId`, and the old `setup_symlink` deliberately left an
   existing link alone — so the stale one won. The tell was a *selective* missing probe (only ones
   newer than that branch), which reads like a bug in the mod's own registration code.
3. **The config swap compounding.** A run that died mid-load left `ModsConfig.xml` replaced by the
   14-mod test list. Every later run then dutifully backed up *that*, so the user's real 828-mod list
   survived only in one old `/tmp/rwth-run-*/` that looked like any other stale temp dir. It sat that
   way for hours, with every run in between passing.

None of these is fixable by asking callers to be more careful; they are all the same shape, which is
**a mutation nobody wrote down**. So the runner now installs the build itself, under the lock
(`--mod-overlay <worktree>`, or `--install <src>:<dst>` with both paths spelled out), and every global
mutation becomes a **claim** recorded in `Runner/asset_claims.py`'s ledger before it happens.

A claim states the prior state explicitly — absent, file, or symlink-to-X — plus a hash of what the
run installed. That explicitness is worth more than the backup: the old code inferred "no prior file
existed" from "no backup was taken", so a run that failed before backing up reached teardown and
*deleted the user's real save*. Teardown is now one rollback of the ledger, newest claim first.

The ledger lives at a fixed path (`/tmp/rwth-claims-<uid>.json`) and exists only while a run holds
something. Because it is read *under the lock*, a ledger that is present can only mean one thing: a
previous run died before cleaning up. The next run rolls it back before taking any claims of its own —
which is what stops failure 3 from compounding.

**Recovery is hash-guarded, and that is what makes it safe to do automatically.** Restoring another
run's backup means restoring something possibly hours old, so each item is rolled back only if it
still hashes to exactly what that run installed. Anything edited since is left alone and reported with
the path to its backup. A wrong automatic restore is worse than a loud manual one — and the common
case (nobody else even knew the file had been swapped) repairs itself silently.

The guard is deliberately *not* applied to a run's own teardown: RimWorld rewrites the whole of
`Prefs.xml` from memory on exit, so a guarded teardown would refuse to undo the `<devMode>` seed every
single time. A run owns what it recorded; the guard is for undoing a run that is no longer around to
vouch for itself.

Two consequences worth stating, because both were learned the hard way:

- **Overlays are directory-granular**, never per-file. Copying just the `.dll` leaves a `.pdb` that no
  longer matches it; Mono faults during assembly load and RimWorld dies with `signo:11` immediately
  after `RWTH: harness loaded`, with the only clue one `Symbol file ... doesn't match image ...` line
  in `Player.log`. It reads as a crash in the mod's own patch code. A whole-directory overlay cannot
  produce a mismatched pair.
- **A type-load failure is now fatal to the run.** A probe bridge built against a different tree than
  the installed DLL throws `ReflectionTypeLoadException` at load; every probe and feature it registers
  silently vanishes, while `Screenshot` steps carry on working. The run yields a full set of plausible
  frames and can even come out green. Since Step 3 writes a minimal modlist, any such exception is
  necessarily ours, so the runner greps `Player.log` for it and fails rather than reporting a result
  that means nothing. `Runner/asset_claims.py` is covered by `Tests/runner/test_asset_claims.py` —
  offline, no game, no lock, ~30 cases pinning each failure above.

### Validated live, 2026-07-30

Four real runs on this box, each checked by md5 on both sides rather than by reading the log:

| | what it proved |
|---|---|
| harness-only, own scenario | 4 claims taken and rolled back; `ModsConfig`/`Prefs` byte-identical afterwards. `Mods/RimWorldTestHarness` was repointed at the harness worktree for the run and handed back — without that, running from a worktree loads the *main* checkout's harness, so the change under test would never have executed. |
| `--mod-overlay` of a branch build | Step 4c fingerprinted `Mods/CelestialLighting -> <main checkout>` while the DLL at that path hashed to the **worktree's** build; scenario passed; teardown restored `.dll` and `.pdb` to the main checkout's hashes exactly. |
| same probe bridge, overlay omitted | Reproduced the split-build failure exactly: `ReflectionTypeLoadException` on `CelestialLighting.AuroraCurtainHemRays`, `ProbeRegistration`'s static constructor dead, every probe gone. The run **failed** naming the cause, where before it would have written a full set of plausible frames. |
| `SIGKILL` mid-run, then recovery | Left the machine dirty in the documented shape (test-list `ModsConfig`, branch DLL over the main checkout, a stray `Mods/TestMod`). `--recover-only` rolled all 8 claims back byte-exact. Repeated with a hand-edit to `ModsConfig` in between: the guard **skipped that one file**, restored the other seven, and parked the ledger under `.unrecovered-<run-id>` so its backup survives without blocking later runs. |

## Timelapse: video as a clock sweep, not a screen recording

A `Timelapse` step produces a video of the map over a span of hours. It is a composite step:
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

### TickLapse: the same output, swept in ticks

`Timelapse` films **the hours changing**. That covers everything whose look is a function of the time
of day, and it is the wrong instrument for an effect that animates on the tick counter — an aurora
curtain, a weather panner, anything with its own motion. Those want the opposite framing: the sun,
the weather and the shadows held still while one thing moves.

`TickLapse` is that sweep. Same numbered-PNG output, same ffmpeg stitch, same left-in-place-on-error
behaviour; the only difference is that each frame advances the clock by `ticks` rather than by hours,
so the whole clip fits inside a single lighting moment. It is not `Timelapse` at a finer setting:
a watchable interval for a drifting aurora is ~10 ticks, which as `stepHours` is `0.004` — a number
that is a transcription exercise to write, tells a reader nothing about the result, and only
round-trips exactly because 2500 ticks/hour happens to divide evenly.

The frames are produced by a new `AdvanceTicks` primitive, and NOT by the existing `FastForward`,
which is the subtler half of the design. `FastForward` raises the game to `Superfast`, waits for a
tick target, and never pauses again — so ticks keep elapsing through the settle frames and the
screenshot flush that follow it. The gap between two captures would then be the requested ticks plus
however many the PNG write happened to cost, a different number every frame, and the clip judders in
a way that reads as the effect stuttering rather than as the camera being uneven. `AdvanceTicks` is a
`DebugSetTicksGame` jump like `AdvanceTime`, so the interval is exact and the frames are evenly
spaced. The cost of a jump is that nothing lives through it — no pawn walks, no fire spreads — which
for this purpose is the feature, not the price.

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

## Reaching an Odyssey orbital map

Everything the harness could reach until now was a *surface* map. Odyssey's orbital maps sit on a
separate `PlanetLayer` (`PlanetLayerDefOf.Orbit`), carry the vacuum `Orbit` biome, and are 200 km up
— and a whole class of lighting behaviour only manifests there. `LandInOrbit` is the step that gets
a scenario onto one.

### Generate the real thing; never dress up a surface map

The cheap version of this step is two lines: keep the fixture's surface map and repoint its tile at
the `Orbit` biome (`SetBiome` already does exactly that). It is also worthless for the thing it would
be used for. Such a map keeps the surface `PlanetLayer`, so its lat/long still comes off the surface
sphere; keeps surface-generated terrain, rock and roof; and reads non-vacuum from per-cell
`VacuumUtility.GetVacuum`, which looks at the room a cell is actually in rather than at a def field.
A probe measuring vacuum lighting against that would validate against a prop and report green — the
single failure mode this repo exists to prevent, dressed as a convenience.

So the step walks vanilla's own route, end to end:

| What | Vanilla call | Why it's the real one |
|---|---|---|
| Find/create the layer | `WorldGrid.PlanetLayers`, else `RegisterPlanetLayer(PlanetLayerDefOf.Orbit, PlanetLayerSettingsDefOf.Orbit.settings)` | The same call `WorldGrid.CreateRequiredLayers` makes at world gen from the Odyssey scenario's `ScenPart_PlanetLayer`. Needed only for a fixture save made before the DLC was installed — which the runner will happily load with the DLC active. |
| Populate it | `PlanetLayer.RunWorldGeneration()` | `WorldGenStep_Tiles` is what stamps each tile with its layer def's `DefaultBiome`. The vacuum biome is *generated onto* the tile, not assigned by us. |
| Generate the map | `GetOrGenerateMapUtility.GetOrGenerateMap(tile, size, layer.Def.DefaultWorldObject)` | The call `SettleInEmptyTileUtility` and every transport-pod arrival use. It creates the `SpaceMapParent`, registers it, and runs `MapGenerator.GenerateMap` with that parent's own `MapGeneratorDef` — Odyssey's `Space` generator. |

Creating the map parent is also what makes the layer legal at all: `RimWorld.OrbitLayer.CanSelectLayer`
refuses the layer until `Find.WorldObjects.AnyWorldObjectOnLayer(this)` is true. Doing the real thing
satisfies that precondition as a side effect, which is why there is no separate "register a dummy
world object" step.

Generation runs **synchronously**, not as a queued long event. The driver already refuses to advance
while `LongEventHandler.ShouldWaitForEvent`, so a queued event would run *after* the step returned and
the postconditions below would have nothing left to check. `MapGenerator.GenerateMap` does not need a
current long event — it only sets and restores `ProgramState`, and `SetCurrentEventText` is a no-op
when no event is running.

### The step checks its own postconditions

Three of them, and the step fails rather than reports success: the map's `PlanetTile.LayerDef` is the
orbit layer, its `BiomeDef.inVacuum` is true, and (on a freshly generated map) its centre cell reads
`GetVacuum > 0`. The third is the one a dressed-up surface map cannot fake — the first two are single
values anything could assign. Without these, a green run would mean "a step that intends to reach
orbit did not throw", which is not the same claim.

### Latitude is required, and pinned anyway

RimWorld's orbits are stationary: `PlanetLayer.LongLatOf` derives lat/long from a fixed tile centre,
and nothing gives an orbit tile a period or a phase. The platform hangs over one lat/long forever, so
that lat/long alone fixes its day length and sun path. A scenario that landed "somewhere in orbit"
would produce numbers nothing could be pinned against, which is the whole point of the harness. Hence
no default.

Naming a latitude is still not enough on its own. The orbit layer is an icosphere subdivided five
times, so its tiles land where the subdivision puts them: "latitude 45" resolves to a tile at 45.32°
on a 30%-coverage world. The step therefore *also* pins `HarnessRuntime.ForcedLatitude`, exactly as
`SetTile` does, so the sun path every probe reads is the latitude that was asked for on every world.
Both numbers go in the run log, because a probe value reconstructed months later has to be traceable
to the one it was computed from.

Longitude is optional and usually best left out. It changes the local-time offset, not the sun's
elevation, and a planet layer is only generated across the world's *view angle*
(`IcosahedronGenerator` takes it as a spherical cap), so on a small-coverage world a specific lat/long
pair may simply never have been generated while its latitude band has thousands of tiles. When the
nearest tile is further than `maxOffsetDegrees` (default 5, a bit over one tile width) the step fails
and names what it found, rather than landing in a band nobody asked for.

### Skip, don't fail, without the DLC

Odyssey is a paid expansion. A box without it cannot be made to run an orbital scenario by editing
any mod, so failing would paint a permanent red on a healthy install. `StepOutcome.Skip(reason)` is
the third outcome alongside success and failure: the driver abandons the rest of that scenario and
records `Skipped` + `SkipReason` on the report.

Abandoning the *scenario* rather than just the step is deliberate. The steps after `LandInOrbit` were
written expecting the world it was supposed to build; running them would pile up failures pointing
anywhere but at the real cause.

`Pass` is deliberately left alone — a skipped scenario with nothing else wrong has no errors and no
probe checks, so the existing gate already computes `true`, and anything recorded before the skip
still counts against it. That means a skip is exactly the case where a green run means less than it
looks like, and the only available mitigation is saying so everywhere the result is read: the finish
line in `Player.log`, the per-scenario line in `run_test.sh`, and a suite-level "skipped N/M
scenario(s), which verified NOTHING" summary.

`Runner/run_test.sh --without-dlc <packageId>` exists so that branch is testable on a machine that
owns the DLC. It deactivates rather than uninstalls, because `ModsConfig.OdysseyActive` reads the
active-mod list. (A fixture save *made* with a DLC cannot be loaded without it — that is vanilla, and
it crashes rather than degrading — so the flag pairs with a `-quicktest` scenario.)

### Residue: `NewMap`, not `Map`

`ScenarioResidue.Map` describes edits to the map a scenario was handed. `LandInOrbit` leaves the
fixture's colony completely untouched and adds a *different* map on a different layer, then switches
to it. The dangerous half is the switch: a following scenario that believed itself isolated would
open on the orbital platform and measure the wrong world while every step in it reported success. A
flag of its own, outside `SoftResettable`, so the planner reloads around it.

## Profiling: borrowing another mod's instrumentation

Our probes answer "how long does one call of this take". They cannot answer "how often does it
happen", and in practice that is the question with the interesting answer — a hook that starts firing
twice as often after a refactor is invisible to every per-call timing we can write. Nor can a probe see
the *shape* of a cost: an average of 0.45 ms hiding a 3.0 ms worst frame is a dropped frame, and a
rolling-refresh design exists precisely to avoid that spike. Building either measurement ourselves
means instrumenting arbitrary methods at runtime, which is a project.

Dubs Performance Analyzer already does it, so `Profile` drives the analyzer rather than reimplementing
it. Everything below follows from that being *someone else's optional Workshop mod*.

### It is reached only by reflection

`Mod/Profiling/DubsAnalyzer.cs` is the only file that names an analyzer type, and it names them all as
strings. A compile-time reference would tie the harness's build to a Workshop download — but the real
problem is what happens at runtime without it: an assembly-level reference is resolved when Mono first
JITs a method touching those types, so an install lacking the analyzer would take a `TypeLoadException`
somewhere unrelated to profiling. Reflection turns absence into a value we can test for, which is what
makes the requirement — no-op *loudly* — implementable at all.

Absence is reported as `StepOutcome.Skip`, the path `LandInOrbit` takes without Odyssey: the scenario
stops, the run stays green, and the report, `Player.log` and the runner's summary all say `SKIPPED`
with the reason. A step that quietly did nothing would leave a green run that verified less than it
claimed, which is the failure this repo exists to prevent.

### Profiling is a property of the run, not of a scenario

This started as a `Profile` step a scenario asked for. That works, and it costs a save reload per
scenario that uses it.

The analyzer instruments by transplanting timing calls into the body of every Harmony-patched method
in the load, and nothing puts those bodies back short of its own `Harmony.UnpatchAll` — which runs
asynchronously and calls `GC.Collect`, not something to do to a live run between scenarios. So
activating it mid-run leaves `ScenarioResidue.Profiler`, outside `SoftResettable`, and a ten-scenario
suite in which every scenario wanted a table would have gone from one boot to nine mid-suite reloads.
That is the exact cost batching scenarios into one load exists to avoid.

Starting the analyzer **once** — after the first map load, before the first scenario's first step —
dissolves the problem rather than paying it. Every scenario in the run is then instrumented
identically, from before any of them ran, so no scenario can contaminate the next one's numbers and
`SuitePlanner.Plan` masks the `Profiler` flag out of every scenario's residue
(`profilerAlreadyActive`). Resetting the analyzer's counters at each scenario's first step and
harvesting at its last turns "one profiler for the run" into "one table per scenario", for free and
with no scenario JSON. `Mod/Profiling/RunProfiler.cs` is the sequencing; `Shared/RunProfiling.cs` is
every decision it makes.

The window opens at the first **step**, not at the scenario boundary: a boundary burns settle frames,
and on the first load the profiler's own warmup. Charging those to the scenario would put a load's
worth of nothing into the divisor under every mean it reports.

Profiling is therefore **on by default**, with `--no-profiler` to opt out. The numbers it produces —
call counts, worst-frame spikes, per-patch attribution — are ones no probe can express at all, and a
feature nobody remembers to switch on is a feature nobody uses. The explicit `Profile` step stays, for
measuring exactly *this* window rather than the whole scenario, and because a `ProfileAssert` can only
target a table that exists before the scenario ends.

### A run that measured nothing must never look like one that measured zero

The cost of default-on is that a profiled run does not always have anything to measure — and a table
of zeroes is the worst possible way to say so. It is not a null result: it is a number that looks like
a measurement, means "nothing was measured", and reads as "this mod is free". Zeroes survive review in
a way an error never does.

`RunProfiling` is therefore two pure classifiers rather than any arithmetic, and each returns a
*reason*:

- **`BeforeStartSkipReason`** — what is knowable before the analyzer is touched. The runner scanned the
  disk, so it is the one that distinguishes "not installed on this machine" from "installed but
  RimWorld did not load it"; its verdict travels in as `RWTH_PROFILE_SKIP` rather than being
  re-derived in-game, because those are different problems with different fixes.
- **`AfterWindowSkipReason`** — every way a window can turn out worthless: the run never became
  interactive (a run that verifies XML patch behaviour and never reaches a map is a normal way to use
  the harness, not an edge case), the scenario opened no window because it ran no steps, no frames
  elapsed, fewer than 30 did, or nothing instrumented ran. The no-game branch is checked first because
  it is the only one that names a *cause*; the rest describe symptoms downstream of it.

The two dispositions differ and the classifier does not care: an explicit `ProfileStop` **fails** (the
scenario asked to measure and got nothing), a run-level harvest records `ProfileSkipReason` (nobody
asked). Sharing one classifier is what stops the two drifting on what "measured nothing" means.

Two things a run-level table discloses rather than skips on. It does **not** force a game speed the way
`ProfileStart` does — that would change what the scenarios it wraps around actually do — so the driver
samples `TickManager.Paused` every frame and the table records `PausedFrames` plus a note. Half a
measurement is still a measurement: the render-path rows are real, and skipping would throw them away
while a silent table would let someone read an absent tick-driven row as a cheap one. And a run-level
table has no `prefix` (nothing in its provenance says which mod the reader cares about), so `Rows` is
capped at 200 — with `Totals` computed over every matched row *before* the cap, because totals derived
from a truncated list would report a subsystem getting cheaper because the report got long.

### The guardrail against comparing across modes

Every timing number in a profiled run — ordinary `Probe` steps included — is measured through a
rewritten build. Pin a probe's `expectedValue` from a profiled run, compare it against an unprofiled
one, and the check moves for a reason that has nothing to do with the code under test. Default-on
makes this *more* likely, not less: a value pinned before this change and re-checked by an ordinary
run today is that mismatch by default.

Every report therefore carries `Profiled`, printed by `run_test.sh` beside `pass=`. That alone is a
weak mitigation, and knowingly so — it is the same weakness as relying on a human noticing `SKIPPED`,
which this document already argues is not enough. So a `Probe` step can additionally record **which
mode its own number was pinned under** (`pinnedUnder`: `any` | `profiler` | `no-profiler`), and a
mismatch is an `Error` on the scenario — failing it, naming the probe, and naming the flag that fixes
it. The precedent is `RWTH_ISOLATION`: a run that isolated less than it was asked to is an error here,
not a note, because it looks identical to one that isolated correctly.

`any` is the default because most probes read state — a season, a latitude, a glow level — and gating
those on the profiler's presence would be noise that teaches people to switch the gate off. The cost
of that choice is that the protection is opt-in per probe, which is a real limitation and is why the
marker exists as well. A misspelled value is rejected at load rather than falling back to `any`: a
guardrail an author believes they have and does not is worse than none.

### Driving a GUI mod headlessly

The analyzer has no headless API; its entry point is a window opening. `DubsAnalyzer.Start()` replays
`Window_Analyzer.PreOpen`'s sequence without the GUI — load the entry list, Harmony-patch
`Root_Play.Update` and `TickManager.DoSingleTick` so the per-frame measurement cycle runs, activate the
Harmony-patches entry, `BeginProfiling`. Three details are load-bearing:

- **Patch through the analyzer's own `Harmony` instance**, not ours, and honour its `Modbase.isPatched`
  flag. Patching under our id would leave the analyzer believing it had never patched, and opening its
  window later would double-count every frame.
- **Force patching synchronous** for the duration (`Settings.disableThreadedPatching`). The analyzer
  normally instruments on a background `Task`; returning early would open the window on a load still
  being rewritten, and the first frames would be missing whichever patches had not been transplanted
  yet, with nothing anywhere saying so.
- **Find the entry by its backing type, not its name.** Entry names go through RimWorld's translation
  system, so name matching would work in English and fail silently in every other locale.

Harvesting reads `Profiler.CollectStatistics` directly rather than `Analyzer.Logs`, because `Logs` is
rebuilt on a background task a couple of times a second — reading it would give a window whose length
we did not choose.

### Three primitives, because the driver only idles between steps

`Profile` desugars into `ProfileStart` / `ProfileMeasure` / `ProfileStop`. The window has to be bounded
by frames that actually render, and the driver only waits *between* steps (`StepOutcome.WaitFrames`);
a single step that "profiles for 600 frames" would have to block inside one `Root_Play.Update` postfix,
during which no frames render and nothing is profiled. `Start` and `Measure` are separate again because
the analyzer rewrites method bodies, so the first call of each pays JIT — `Start` warms, `Measure`
zeroes the counters and opens the real window. Written out by hand, the primitives also let a scenario
profile *a span of steps* (a `Timelapse`, say) rather than a span of idle frames.

`ProfileStart` also forces a game speed, defaulting to `normal` rather than leaving whatever the
scenario had. A scenario that jumped the clock leaves the game paused, and profiling a paused colony
records a load of tick-driven patches that never fire — a table of near-zeroes reading as "this mod is
free".

### Two numbers that lie if reported alone

The arithmetic lives in `Shared/ProfileMath.cs`, unit-tested offline, because every derived figure here
is a division whose wrong answer is plausible rather than obviously broken.

- **The percentage.** The analyzer reports a share of the frame the machine *achieved*, and a headless
  harness run hits ~350 fps. A row at 15.9% of a 2.85 ms frame is ~2.7% of a 60 fps budget — the bare
  percentage reads six times more alarming than it is. Tables therefore record `FrameMs` and
  `FramesPerSecond` beside the percentages, and every row carries `PercentOfSixtyFpsBudget` as well.
- **The mean per call.** Call counts include invocations that hit a guard clause and return, so
  `AvgUsPerCall` averages expensive and trivial calls together and *understates* the cost of the ones
  that do work. `MaxCallsPerFrame` is the only shape information the analyzer gives us behind that
  mean. Both caveats also ride in each table's `Notes`, because the moment someone misreads them is
  while looking at a report, not while reading this file.

Two more things an explicit `Profile` step refuses rather than reports: a window in which the analyzer
recorded no frames (`CollectStatistics` divides by that count, producing `NaN` averages that are both
unserializable and indistinguishable from "free"), and a `prefix` matching no rows (an empty table
reads the same way, and the likely cause is a renamed namespace or a mod missing from the run's
`ModsConfig`).

Tables themselves never gate `Pass`; only a `ProfileAssert` step does. The primary use is diffing the
same table between builds, and a run going red because the machine was busy would train everyone to
ignore the colour. `ProfileAssert` bounds are `max` / `min` / `expectedValue`+`tolerance`, and the
one-sided forms exist because performance assertions almost always are — expressing "at most 1 ms" as
`expectedValue: 0.5, tolerance: 0.5` is a gate nobody writes twice. That is what `ProbeCheckResult`
gained a `Comparison` field for.

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

`DubsAnalyzerApiTests` pins the analyzer's surface in the same project, ignored when it isn't
installed. It matters more than the vanilla fixture beside it: everything `DubsAnalyzer` touches is
reflection over an optional mod on nobody's release cadence but its own, so none of it is
compiler-checked. `Profiler.CollectStatistics`'s **out-parameter order** in particular is pinned,
because it is read back positionally — a reordering there would swap max and total time and produce an
entirely plausible wrong report.
