# RimWorldTestHarness

A dev-only RimWorld 1.6 mod plus a driver script that turns "load the game and eyeball it" into a
repeatable, scriptable test pass. You write a **scenario** — a small JSON file describing a save to
load, a clock/latitude/season to force, and a list of steps to run — and the harness launches
RimWorld, replays those steps against a live map, and writes a JSON report.

Two kinds of verification share one scenario format:

- **Probes** — a scenario asserts that some numeric quantity your mod computes is within tolerance
  of an expected value. This is a real pass/fail gate: the runner exits non-zero when it fails.
- **Screenshots** — a scenario captures rendered frames (optionally a whole timelapse, stitched to
  video) for a human or an AI agent to review afterward.
- **Vision asserts** — a scenario declares a *rubric* for an LLM judge, and the run emits it with the
  named screenshots and the game's recent warnings/errors attached. This catches what a probe cannot:
  a formula returning the right number while nothing reaches the screen.

None of these excludes the others. A single scenario can pin a number, leave you images of the same
moment, and say in words what those images should show.

> **Status:** working end-to-end. Single scenarios and screenshots/timelapses are confirmed against
> live runs; the mid-suite save reload is implemented and offline-tested but not yet live-verified.
> See `TODO.md` for the current checklist and `DESIGN.md` for why things are built the way they are.

---

## Why this exists

Offline unit tests can cover a mod's pure math, and Mono.Cecil API tests can prove the vanilla
members you patch still exist. Neither tells you whether the thing actually *looks right and behaves
right inside a running game*. Manual spot-checking does, but it doesn't scale, and it leaves nothing
behind — the next person (or the next Claude session) can't re-run your eyeballs to catch a
regression. This harness is the third leg: end-to-end checks against a real game, expressed as files
you can commit.

## How it works

The mechanism is deliberately boring, because clever launch tricks break on RimWorld updates:

1. `Runner/run_test.sh` symlinks the mods under test into RimWorld's `Mods/` folder, backs up your
   real `ModsConfig.xml` and `Saves/autostart.rws`, and writes a minimal mod list (Core + installed
   DLCs + Harmony + your scenario's `requiredMods` + the harness).
2. It copies your fixture save to `Saves/autostart.rws`. **RimWorld's own vanilla autostart
   mechanism** then loads it at boot — no custom load-driving code. (Autostart is gated on dev mode,
   which a Harmony patch forces on while a scenario is active.)
3. It launches RimWorld with `RWTH_SCENARIO` (or `RWTH_SUITE`) and `RWTH_REPORT` in the environment,
   with real GPU rendering — no `-batchmode`, because screenshots need actual rendered frames.
4. Inside the game, `ScenarioDriver` runs as a tick-driven state machine, executing one step per
   opportunity and folding each result into a report.
5. The script waits for the report file, prints every probe result and screenshot path, stitches any
   timelapse frames into video, and **exits 0 only if the report says `Pass`**.
6. It restores your `ModsConfig.xml` and `autostart.rws` and removes any symlink it created.

## Requirements

- RimWorld 1.6 with Harmony installed.
- .NET SDK able to target `net481` (on Linux, Mono's reference assemblies via
  `FrameworkPathOverride=/usr/lib/mono/4.8-api`).
- `jq`, `python3`, and `flock` on `PATH` — the runner checks for all three up front and refuses to
  start without them. `ffmpeg` too, if you want timelapse frames stitched into video.
- A **fixture save** — see below. This is the one step that cannot be scripted.

## Quick start

```bash
./build.sh    # builds Mod/ (net481) -> 1.6/Assemblies/
./test.sh     # runs the offline unit tests + the API-compatibility tests

Runner/run_test.sh Scenarios/daycycle_timelapse.json
```

Reports and screenshots land in `Runner/reports/` (gitignored), timestamped per run.

### The fixture save

Scenarios load a real save. There is no headless "generate a colony" API to script from outside the
game, so each fixture is created once, by hand:

1. Launch RimWorld with only Core + Harmony active.
2. Start a colony on **any tile** — landing latitude doesn't matter, since `SetTile` steps override
   latitude at runtime via a Harmony patch on `WorldGrid.LongLatOf`. One fixture serves every
   scenario that differs only in time/season/latitude.
3. Get it stable and saveable, then save it into `Fixtures/` under the name your scenarios'
   `saveFile` field expects.

Keep fixtures small — few pawns, little mod content — so loads stay fast and the save is easy to
regenerate for a new RimWorld version. `Fixtures/` is gitignored: `.rws` files are binary-ish XML
blobs tied to a specific mod list and don't diff usefully.

If no fixture exists, the runner falls back to `-quicktest`, which generates a colony at boot. That
works for a single scenario but writes no save, so there is nothing to reload from mid-suite.

---

## Writing a scenario

A scenario is JSON with three fields and a list of steps:

```json
{
  "name": "shadow_lean_equinox",
  "saveFile": "minimal_colony.rws",
  "requiredMods": { "some.packageid": "2009463077" },
  "steps": [
    { "type": "SetTile",    "args": { "latitude": "55" } },
    { "type": "SetSeason",  "args": { "dayOfYear": "15" } },
    { "type": "SetTime",    "args": { "hour": "15" } },
    { "type": "Probe",      "args": { "probeName": "shadow_lean", "expectedValue": "0", "tolerance": "0.02" } },
    { "type": "Screenshot", "args": { "fileName": "shadow_lean_equinox.png" } }
  ]
}
```

- `saveFile` is relative to `Fixtures/`.
- `requiredMods` maps packageId → Steam Workshop file id. It's the single source of truth for both
  `Runner/fetch_mods.sh` (which downloads anything missing) and the mod list the runner writes, so
  the two can't drift apart.
- `args` values are **always strings**, even numbers and booleans. The arg bag is deliberately
  untyped so new step kinds don't require regenerating C# types; `Shared/StepArgs.cs` is the
  canonical list of key names and `Shared/StepValidator.cs` rejects unknown types and bad scene args
  at load time, before the game spends minutes producing garbage.

Scenarios live wherever they make sense. The path is just an argument, so a mod's own scenarios
belong in that mod's repo (`SomeMod/Tests/Scenarios/*.json`); only modset-agnostic demos live in
this repo's `Scenarios/`.

### Step reference

**Clock and world**

| Type | Args | Notes |
|---|---|---|
| `SetTile` | `latitude` | float, −90..90. Patches `LongLatOf`, so the fixture's real tile is irrelevant. |
| `SetSeason` | `dayOfYear` | int, 0..59. |
| `SetTime` | `hour` | float, 0..24. |
| `FastForward` | `ticks` | int. Advances the game clock by running ticks. |
| `Wait` | `frames` | int ≥ 0. Idles for rendered frames — use it to let visuals settle. |
| `StartCondition` | `conditionDef`, `durationHours`, `agedHours` | Starts a vanilla `GameConditionDef` (`SolarFlare`, `Eclipse`, …). `durationHours` defaults to 24; ≤ 0 means permanent. `agedHours` back-dates the start tick so the condition is "born aged" and any fade-in has already elapsed. |
| `SetWeather` | `weatherDef`, `instant` | Transitions to a `WeatherDef` (`Rain`, `Clear`, `Fog`, …) — weather drives sky glow and shadow strength. `instant` defaults to true and completes the blend immediately; false leaves the natural transition. |
| `SetBiome` | `biomeDef` | Repoints the map's world tile at a `BiomeDef` (`Undercave`, `IceSheet`, `Orbit`, …). A lot of lighting behaviour is gated on the biome rather than the map — `disableSkyLighting`, `disableShadows`, and `baseWeatherCommonalities` deciding whether the map has changeable weather at all — and with one save fixture this is the only way to reach those branches. Dirties `ScenarioResidue.Biome`, which requires a reload, so a suite will isolate around it. |

**Verification**

| Type | Args | Notes |
|---|---|---|
| `Probe` | `probeName`, `expectedValue`, `tolerance` | `probeName` must match a registered `IProbe.Name`. This is what decides pass/fail. |
| `Screenshot` | `fileName`, `hideUi` | Written next to the report. `hideUi` defaults to `true` (blanks the HUD via RimWorld's screenshot mode). |
| `SetFeature` | `featureName`, `enabled` | Flips a feature flag your mod registered. The point is A/B: screenshot with an effect off, flip it on, screenshot again — in one boot. |
| `Assert` | `kind`, `images`, `prompt`, `expect`, `confidenceGate`, `logLines` | `kind: vision` — a rubric for an LLM judge over named screenshots plus the game's recent warnings/errors. Soft gate: only a *confident* fail blocks. See `Runner/README.md`, "Vision asserts". |

**Scene setup** — build something worth looking at, at runtime, instead of authoring it into the
save. Shared by `PlaceThings` / `SetTerrain` / `LookAt` / `SpawnPawn`: `anchor` (`"center"` by
default, or absolute cells like `"125,125"`), `offset` (`"dx,dz"`), and `unfog` (default **true** —
fogged cells draw neither terrain nor things, so a scene built in fog is invisible while every step
still reports success). `PlaceThings` / `SetTerrain` / `SpawnPawn` also take `clear` (default **false**
— it permanently destroys whatever occupies the footprint, so it's opt-in; a roofed footprint still warns).

| Type | Args | Notes |
|---|---|---|
| `PlaceThings` | `def`, `stuff`, `rot`, `layout`, `cols`/`rows`/`spacing`, `count`/`axis`, `cells` | `layout` is `grid` (default), `row`, or `cells` (`"0,0; 4,0; 8,-3"`). `rot` is `North`/`East`/`South`/`West` — a lowercase typo fails the scenario rather than silently meaning North. |
| `SetTerrain` | `def`, `width`, `height` | Paints a rectangle of a `TerrainDef`. |
| `SetRoof` | `def`, `width`, `height` | Paints a rectangle of a `RoofDef` (`RoofConstructed`, `RoofThin`, …), or strips roof where `def` is `None`. No `clear` — roofing destroys nothing. Roof is the one map state no other step could produce: the game only ever roofs a cell as a consequence of *play*, so a scenario that spawns walls otherwise gets an unroofed shell. Roof is painted over whatever stands in the rect, walls included, which is how you build an **eave** — roofed cells no wall encloses. Defaults to 7x7, deliberately smaller than `SetTerrain`'s 40x40. |
| `LookAt` | `zoom` | Aims the camera at the anchor. Omit `zoom` to keep the current one. |
| `SpawnPawn` | `kind`, `faction`, `gender`, `hediffs`, `count`, `spacing`, `clear` | Generates pawns in a row along +x from the anchor. `kind` is a `PawnKindDef` defName (`Muffalo`, `Colonist`, `Pirate`). `faction` is `wild` (default, no faction — animals/wild men), `player` (your colony), or `hostile` (a deterministic enemy faction). `gender` is `male`/`female` (omit for random). `hediffs` applies health conditions: `"Flu:0.4; MissingBodyPart@Leg; BionicArm@Arm"` — each is a `HediffDef`, optionally `@BodyPartDef` to target a part and/or `:severity`; an unknown def or a part the race lacks fails the step before any pawn spawns. `count` defaults to 1, `spacing` to 2. `clear` (default false) bulldozes the spawn cells first. Any cell that still can't take a pawn is reported, not silently skipped. |

**Composite**

| Type | Args | Notes |
|---|---|---|
| `Timelapse` | `fromHour`, `toHour`, `stepHours`, `fileNamePrefix`, `settleFrames`, `fps` | Desugared at load time into a `SetTime` / `Wait` / `Screenshot` triple per frame, so nothing downstream needs to know it exists. Defaults: `0` → `24` exclusive, `1`-hour steps, prefix `timelapse`, `settleFrames` 2, `fps` 12. The runner stitches the frames into an mp4. |

A timelapse is a **clock sweep, not a screen recording** — each frame is a still capture after the
clock is jumped and the render is allowed to settle, which is why `settleFrames` exists.

### A richer example

```json
{
  "name": "aurora",
  "saveFile": "minimal_colony.rws",
  "steps": [
    { "type": "SetTile", "args": { "latitude": "20" } },
    { "type": "SetSeason", "args": { "dayOfYear": "40" } },
    { "type": "SetTime", "args": { "hour": "1" } },
    { "type": "SetFeature", "args": { "featureName": "pitch_black_nights", "enabled": "false" } },
    { "type": "StartCondition", "args": { "conditionDef": "SolarFlare", "durationHours": "24", "agedHours": "2" } },
    { "type": "Probe", "args": { "probeName": "aurora_tint", "expectedValue": "0.35", "tolerance": "0.02" } },
    { "type": "Screenshot", "args": { "fileName": "aurora_warmup.png" } },
    { "type": "SetFeature", "args": { "featureName": "aurora", "enabled": "false" } },
    { "type": "Screenshot", "args": { "fileName": "aurora_off.png" } },
    { "type": "SetFeature", "args": { "featureName": "aurora", "enabled": "true" } },
    { "type": "Screenshot", "args": { "fileName": "aurora_on.png" } }
  ]
}
```

That's a numeric gate and a three-image A/B comparison from one boot.

---

### Adding a step type

Steps are a registered extension point: implement `IStepSpec` (pure — name, residue, validation) in
`Shared/Steps/` and `IStepAction` (the live-game half) in `Mod/Steps/`, and both registries find them
by reflection at startup. No switch, list, or registration call needs editing, including from a
third-party mod's own assembly. `SetWeather` is the worked example, in two small files.

See **[CONTRIBUTING.md](CONTRIBUTING.md)** for the walkthrough and `DESIGN.md`'s "Step registration"
for why the definition is split across two assemblies.

## Making your mod testable

The harness **never** references the mod under test, and your shipped mod must never reference the
harness — it's a dev-only tool and shipping a hard dependency on it would be a mistake. The two meet
in a small third assembly: a dev-only "probes" mod that references both.

### 1. Write a probe

A probe is one number the harness can assert on:

```csharp
using RimWorldTestHarness.Mod.Probes;
using Verse;

public sealed class ShadowLeanProbe : IProbe
{
    public string Name => "shadow_lean";

    public float Read(Map map)
    {
        Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
        int dayOfYear = GenDate.DayOfYear(Find.TickManager.TicksAbs, longLat.x);
        return Formulas.SolarDeclinationDegrees(dayOfYear);   // your mod's own pure formula
    }
}
```

Implement the optional `IProbeMetadata` alongside it if you want the live catalog to describe what
the number means and what unit it's in.

### 2. Register probes and feature flags

```csharp
[StaticConstructorOnStartup]
public static class ProbeRegistration
{
    static ProbeRegistration()
    {
        ProbeRegistry.Register(new ShadowLeanProbe());

        // Exposes a runtime flag to the SetFeature step. The third arg is the resting value
        // ResetAll() restores between scenarios; it defaults to true.
        FeatureRegistry.Register("aurora", enabled => MyModFeatures.Aurora = enabled);
    }
}
```

Registration is explicit today rather than reflection-discovered (see the TODOs in
`ProbeRegistry.cs` / `FeatureRegistry.cs`).

### 3. Wire up the bridge project

Keep the probe sources out of your shipped assembly (`<Compile Remove="Probes/**/*.cs" />` in the
main csproj) and `<Compile Include>` them into the dev-only one, so there is exactly one copy on
disk rather than a copy that can drift:

```xml
<PropertyGroup>
  <TargetFramework>net481</TargetFramework>
  <AssemblyName>MyMod.Probes</AssemblyName>
  <OutputPath>1.6/Assemblies</OutputPath>
</PropertyGroup>

<ItemGroup>
  <Compile Include="../Source/Probes/ShadowLeanProbe.cs" Link="ShadowLeanProbe.cs" />
</ItemGroup>

<ItemGroup>
  <Reference Include="MyMod">
    <HintPath>../1.6/Assemblies/MyMod.dll</HintPath>
    <Private>false</Private>
  </Reference>
  <Reference Include="RimWorldTestHarness">
    <HintPath>../../RimWorldTestHarness/1.6/Assemblies/RimWorldTestHarness.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

Build your mod and the harness before this project — it references their built output, not their
projects, because the dependency must only ever point one way.

This repo deliberately ships no example mod under test — nothing in its code, tests, or scenarios
references one, so the harness builds and its full test suite passes standalone. (In this author's
setup, a sibling `CelestialLighting/TestMod/` is the working reference implementation of all three
steps, but it is not required and not distributed here.)

---

## Suites: several scenarios in one boot

A boot costs minutes; a step costs milliseconds. Give the runner more than one scenario and they all
run inside a single game load:

```bash
Runner/run_test.sh a.json b.json c.json          # explicit list
Runner/run_test.sh ../MyMod/Tests/Scenarios/*.json   # the shell globs; the harness never does
Runner/run_test.sh --suite Scenarios/demo_suite.txt  # a checked-in list file
```

A suite list is one path per line; whole-line `#` comments and blanks are ignored, and relative paths
resolve against the list file's own directory. (Trailing `#` is *not* a comment — `#` is legal in a
filename, and truncating a path would look like a missing file.)

Between scenarios the driver restores what it cheaply can — clock, latitude, feature flags, time
speed, camera, screenshot mode — and **reloads the save mid-session** where it can't, which in
practice means after any scenario that ran `PlaceThings`/`SetTerrain`. Map mutations aren't undoable.
`--isolation=auto|always|never` overrides that policy (`always` reloads before every scenario;
`never` only soft-resets, and records the shortfall in the report so a green suite says out loud that
scenarios shared a world).

Two things worth knowing:

- **Every scenario in one run must declare the same `saveFile`.** The fixture is installed once at
  boot and reloaded from mid-run. Mixed fixtures are rejected up front rather than silently run
  against whichever came first.
- **Order matters for cost, and the runner never reorders.** Put map-mutating scenarios last and the
  suite pays for fewer reloads. Reordering would make a scenario's result depend on what else was in
  the run.

Screenshot names are prefixed with their scenario (`<scenario>__<fileName>`) so independently
authored scenarios can't overwrite each other's images.

### Runner flags

| Flag | Effect |
|---|---|
| `--mod <folder>` | Activate a mod alongside the harness. Repeatable, and optional — this repo's own scenarios need none. |
| `--suite <list.txt>` | Run the scenarios named in a list file. |
| `--isolation=auto\|always\|never` | How hard a suite works to isolate one scenario from the next. |
| `--no-teardown` | Leave symlinks / `ModsConfig` / `autostart.rws` in place for post-mortem debugging. |
| `--delete-frames` | Delete timelapse PNGs once stitched into video. |
| `--print-config` | Print every path the run would use and exit — touching, locking, and creating nothing. |

Overridable environment: `RWTH_CONFIG_DIR` (game save-data root), `RWTH_RUN_TMP_DIR`,
`RWTH_LOCK_FILE`, and `RWTH_ISOLATE_SAVEDATA=1` (opt-in: gives the run its own save-data root via
RimWorld's `-savedatafolder=` arg instead of mutating yours — implemented and asserted, but not yet
validated by a live run).

---

## Reading the result

The report is JSON in `Runner/reports/`. A single scenario:

```json
{
  "ScenarioName": "shadow_lean_equinox",
  "Pass": true,
  "ProbeChecks": [
    { "ProbeName": "shadow_lean", "ActualValue": 1.02e-06, "ExpectedValue": 0.0,
      "Tolerance": 0.02, "Pass": true }
  ],
  "ScreenshotPaths": ["…/shadow_lean_equinox.png"],
  "Errors": []
}
```

A suite wraps one unchanged `ScenarioReport` per scenario in a `SuiteReport` with `Pass`, `Errors`,
and `IsolationNotes`; you tell the two apart by the presence of a `Scenarios` key. The single-scenario
shape is byte-identical to what it always was, so existing consumers keep working.

The gate is stricter than "all probes passed", on purpose. A run fails if any probe check fails, **or
if there were any errors at all** — because a scenario whose every step errored has zero probe checks,
and "nothing was verified" otherwise reads exactly like "everything passed". Same reasoning at the
suite level: an empty suite fails, and scenarios a mid-suite abort never reached are listed with an
explicit "did not run" error rather than quietly omitted. Screenshots never affect `Pass`; they're a
complementary review channel, not a gate.

---

## Live companion channel (interactive mode)

Besides batch scenarios, the mod has an interactive mode: tick a mod-settings checkbox and it watches
a session directory (`$XDG_CACHE_HOME/rimworld-dev-mcp/live`) for one-off commands, runs each on the
main tick thread, and writes back results, a heartbeat, and a **catalog** of what's available. Batch
answers "did this scenario pass?"; the companion answers "let me poke whatever's on screen right
now." It's driven by a sibling `RimWorldDevMCP` server / `rwdev` CLI.

Design constraints worth knowing if you use it:

- It's armed by a **setting**, not an env var, so it works against a game launched normally through
  Steam. Off by default; the mod stays inert until ticked.
- It bails immediately while a batch scenario is active — the two never run together.
- It's minimally invasive: it never forces dev mode and never touches time speed except for an
  explicit `FastForward`, which restores the prior speed. Idle costs ~nothing per frame.
- The scene-setup verbs (`PlaceThings`, `SetTerrain`, `LookAt`) are deliberately **not** exposed.
  They mutate the colony and `PlaceThings` spawns with `WipeMode.Vanish`; silently bulldozing part of
  someone's real base over a file-drop channel would break the "minimally invasive" promise. Those
  verbs belong in scenario runs, which load a throwaway fixture and never save it.
- The catalog is **discovered, not hardcoded**: it reflects over `Verse.GenTypes.AllTypes` for
  `[LudeonTK.DebugAction]` methods — the same set the game's own dev menu shows — plus registered
  probes and feature flags, so it mirrors whatever modset is actually loaded. Only zero-arg
  `DebugActionType.Action` entries are invokable headlessly; tool-type actions that need a click
  target are listed but not yet callable.

---

## One run at a time

A run mutates global machine state, so it defends itself rather than trusting it's alone:

- It **refuses to start** if any `RimWorldLinux` is already running, or if another `run_test.sh` holds
  the exclusive run lock. Both are hard failures with a message, not waits.
- It only ever signals the game process **it** started, by PID (SIGTERM, escalating to SIGKILL after
  10s). It does not `pkill` by name, so your own play session survives.
- Every mutable file a run owns — `ModsConfig`/`autostart` backups, its `Player.log`, the game's
  stderr — lives under one per-run scratch directory, so two overlapping runs can't restore each
  other's backup over your real config. `Player.log` in particular *had* to move there: the script
  greps it for progress markers, so it can't be shared.

---

## Repository layout

| Path | What's in it |
|---|---|
| `Shared/` | Pure spec/report/planner logic (`netstandard2.0`, no game dependency). Fully unit-tested offline. `Steps/` holds each step's game-free half. |
| `Mod/` | The in-game driver (`net481`, Harmony): bootstrap, `ScenarioDriver` state machine, `StepExecutor`, `SceneBuilder`, `LiveCommandDriver`, patches, `Probes/`, `Features/`, and `Steps/` for each step's live-game half. |
| `Runner/` | `run_test.sh` (launch/wait/gate) and `fetch_mods.sh` (Workshop dependency download). |
| `Scenarios/` | Modset-agnostic example scenarios and a suite list. |
| `Fixtures/` | Save files scenarios load from. Gitignored; created manually. |
| `Tests/RimWorldTestHarness.Tests/` | NUnit tests for `Shared/`. |
| `Tests/RimWorldTestHarness.ApiTests/` | Mono.Cecil checks that the vanilla RimWorld/Unity API surface `Mod/` depends on still exists in the installed game. Run after every RimWorld update. |

The split between `Shared/` and `Mod/` is the same discipline the rest of these repos use: anything
with nontrivial branching or arithmetic goes in a dependency-free assembly that takes primitives and
returns primitives, and the code touching live `Map`/`Find` state stays a thin adapter. That's why
scene layouts can be dry-run validated at load time without a `Map`, and why the pass/fail comparer
has edge-case tests.

## Adapting it to your own setup

One thing is still hardcoded: `Runner/run_test.sh` assumes a Steam RimWorld install at
`/home/deck/.local/share/Steam/steamapps/common/RimWorld` and a Linux config dir. `RWTH_CONFIG_DIR`
covers the save-data root; the install path itself is a one-line edit near the top of the script.

The mod under test is **not** hardcoded — pass `--mod <folder>` once per mod (see the flags table
above). The runner reads each mod's `<packageId>` from its own `About/About.xml`, symlinks the folder
into `Mods/`, and appends the harness last so its patches wrap the mod's. Nothing about a specific
mod appears anywhere in this repo's code or tests.

`Runner/fetch_mods.sh <scenario.json>` downloads a scenario's `requiredMods` via `steamcmd` using an
**anonymous** login — logging in with a real account would take over the machine's cached Steam
session and break the next launch through Steam. Caveat: Steam sometimes refuses anonymous
`workshop_download_item` for paid apps like RimWorld. There's no credential fallback by design; if it
fails, subscribe through the in-game Workshop UI instead.

## Gotchas

- **Screenshots need a real GPU-rendered frame** — the runner deliberately does not pass
  `-batchmode`/`-nographics`.
- **Fog hides your scene.** A freshly generated colony reveals only a small pocket, and RimWorld draws
  neither terrain nor things in fogged cells. `unfog` defaults to true for this reason; if you set it
  false you can get a perfectly green run over an empty-looking image.
- **`override` methods are the classic silent breakage.** If RimWorld renames a base method, your
  override compiles fine and is simply never called. That's what `Tests/RimWorldTestHarness.ApiTests`
  is for — run `./test.sh` after every RimWorld update, before you load the game.
- **A failing step is never silently dropped.** Bad steps stay in the list and fail again at
  execution, and loader errors travel into the report, because a scenario that quietly ran fewer steps
  would verify less than it claims to. Nearly every design decision in this repo comes back to that
  one rule: a green run must never mean less than it looks like.

Further reading: `DESIGN.md` (architecture and the reasoning behind each decision), `TODO.md`
(what's left), `Runner/README.md` (runner specifics), `Fixtures/README.md` (fixture creation).
