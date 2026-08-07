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
| `SetTime` | `hour` | float, 0..24. ABSOLUTE, and it pins the current day — asking for `0` right after `23.75` rewinds the clock nearly a whole day rather than stepping forward. For a sequence use `AdvanceTime`. |
| `AdvanceTime` | `hours` | float, > 0. Moves the clock FORWARD by a duration, rolling into the next day as the game would. A jump, not simulation (same as `SetTime`, unlike `FastForward`, which runs real ticks). Exists because a sweep built from absolute `SetTime`s silently rewinds at midnight: hour-of-day effects can't tell, but anything driven by absolute time can — a lunar cycle jumped a day backwards and swung its shadows mid-timelapse. |
| `FastForward` | `ticks` | int. Advances the game clock by running ticks. |
| `AdvanceTicks` | `ticks` | int, > 0. Moves the clock FORWARD by an exact number of ticks WITHOUT simulating them — `AdvanceTime` in the unit the tick counter is measured in. Exists for effects that animate on `TicksGame` (an aurora curtain, a weather panner). The hours that express a watchable interval for one are unreadable: 12 ticks is 0.0048 hours. And `FastForward` cannot hold an interval steady — it leaves the game running at `Superfast`, so ticks keep elapsing through the settle frames and the screenshot flush that follow, and consecutive captures end up unevenly spaced. Nothing lives through a jump (no pawn walks, no fire spreads), which is the point: everything holds still except what reads the tick counter. |
| `Wait` | `frames` | int ≥ 0. Idles for rendered frames — use it to let visuals settle. |
| `StartCondition` | `conditionDef`, `durationHours`, `agedHours` | Starts a vanilla `GameConditionDef` (`SolarFlare`, `Eclipse`, …). `durationHours` defaults to 24; ≤ 0 means permanent. `agedHours` back-dates the start tick so the condition is "born aged" and any fade-in has already elapsed. |
| `SetWeather` | `weatherDef`, `instant` | Transitions to a `WeatherDef` (`Rain`, `Clear`, `Fog`, …) — weather drives sky glow and shadow strength. `instant` defaults to true and completes the blend immediately; false leaves the natural transition. |
| `SetTileProperties` | `elevation`, `pollution`, `rainfall` | Overwrites scalar fields on the map's world `Tile`. All three are optional and at least one is required; an omitted field is left alone rather than reset. `elevation` is metres above sea level (unbounded — worldgen produces negative values for ocean tiles), `pollution` is 0–1, `rainfall` is mm/year. Distinct from `SetTile`, which forces *latitude* via a harness-side override that touches no world state; these are real mutable fields on `RimWorld.Planet.Tile`, so this writes through and dirties `ScenarioResidue.TileProperties`, which requires a reload. All three are on base `Tile` and need no DLC. |
| `SetBiome` | `biomeDef` | Repoints the map's world tile at a `BiomeDef` (`Undercave`, `IceSheet`, `Orbit`, …). A lot of lighting behaviour is gated on the biome rather than the map — `disableSkyLighting`, `disableShadows`, and `baseWeatherCommonalities` deciding whether the map has changeable weather at all — and with one save fixture this is the only way to reach those branches. Dirties `ScenarioResidue.Biome`, which requires a reload, so a suite will isolate around it. |
| `LandInOrbit` | `latitude`, `longitude`, `maxOffsetDegrees`, `mapSize`, `unfog` | **Generates a real Odyssey orbital map** at the requested lat/long and switches to it, so every later step in the scenario runs on the platform. `latitude` (−90..90) is required; `longitude` (−180..180) is optional and usually best omitted. See below. |

`LandInOrbit` is not `SetBiome` with extra steps. It resolves a tile on the `Orbit` `PlanetLayer` and
runs Odyssey's own space map generator for it through `GetOrGenerateMapUtility` — the same call
settling a caravan makes. The layer, the vacuum `Orbit` biome and per-cell
`VacuumUtility.GetVacuum` are all arrived at *by generation*, and the step fails if any of them
comes out wrong. Repointing a surface map's biome instead would leave the surface `PlanetLayer`,
surface lat/long, surface terrain and non-vacuum cells — a prop that anything measuring vacuum
lighting would happily validate against.

- **`latitude` is required, not defaulted.** RimWorld's orbits are stationary, so the platform's
  latitude alone fixes its day length and sun path. It is also *pinned* the way `SetTile` pins one
  (`WorldGrid.LongLatOf` is patched), because the orbit layer's tiles are a couple of degrees wide
  and land where the icosphere subdivision puts them, not where you asked. The run log prints both
  the tile's real lat/long and the pinned value.
- **Omit `longitude` unless you need it.** Longitude changes the local-time offset, not the sun's
  elevation, and a planet layer is only generated across the world's *view angle* — so on a
  small-coverage world a specific lat/long pair may not exist while its latitude band has plenty of
  tiles. With no longitude the step takes the nearest tile in that band.
- **It fails loudly if it can't get close.** `maxOffsetDegrees` (default 5) is how far the resolved
  tile may sit from the request; past that the step fails and names the nearest tile it found, rather
  than landing in a latitude band nobody asked for.
- **`mapSize`** (50..1000, default the world's own) — map generation is the most expensive thing in
  the harness, so a probe-only scenario should ask for a small one. **`unfog`** defaults to `true`
  because the space generator fogs the map and fogged cells draw nothing.
- **Without Odyssey it SKIPS, it does not fail.** The scenario stops at that step, is reported
  `Skipped` with a reason, and the run stays green — a paid DLC missing is not something editing a
  mod can fix. See "Skipped scenarios" below.
- Dirties `ScenarioResidue.NewMap | ScenarioResidue.Latitude`; `NewMap` requires a reload, so a suite
  isolates around it.

```json
{ "type": "LandInOrbit", "args": { "latitude": "45", "mapSize": "150" } }
```

**Verification**

| Type | Args | Notes |
|---|---|---|
| `Probe` | `probeName`, `expectedValue`, `tolerance`, `pinnedUnder` | `probeName` must match a registered `IProbe.Name`. This is what decides pass/fail. `pinnedUnder` (`any` default \| `profiler` \| `no-profiler`) records which profiling mode `expectedValue` was measured under and **fails the scenario** on a mismatch — see "Profiling" below. |
| `Screenshot` | `fileName`, `hideUi` | Written next to the report. `hideUi` defaults to `true` (blanks the HUD via RimWorld's screenshot mode). |
| `SetFeature` | `featureName`, `enabled` | Flips a feature flag your mod registered. The point is A/B: screenshot with an effect off, flip it on, screenshot again — in one boot. |
| `Assert` | `kind`, `images`, `prompt`, `expect`, `confidenceGate`, `logLines` | `kind: vision` — a rubric for an LLM judge over named screenshots plus the game's recent warnings/errors. Soft gate: only a *confident* fail blocks. See `Runner/README.md`, "Vision asserts". |
| `ProfileAssert` | `table`, `label`, `metric`, `max` \| `min` \| `expectedValue`+`tolerance` | Checks one number out of a profile table (see "Profiling" below). Lands in the same `ProbeChecks` gate a `Probe` step does. |

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
| `SetSnow` | `depth`, `width`, `height` | Lays snow over a rectangle by writing depth straight into the grid: `depth` is 0..1 (0 clears, 1 is vanilla's `SnowGrid.MaxDepth`), default 1 over 40x40. Snow is an *accumulation*, not a weather state — `SetWeather SnowHard` only starts flakes falling and depth then grows as a function of the map's temperature, so on a warm tile it never arrives at all. Without this step a snowy scene is unfilmable except by swapping biome and burning minutes of `FastForward`, which still fails above freezing. |
| `LookAt` | `zoom` | Aims the camera at the anchor. Omit `zoom` to keep the current one. |
| `SpawnPawn` | `kind`, `faction`, `gender`, `hediffs`, `count`, `spacing`, `clear` | Generates pawns in a row along +x from the anchor. `kind` is a `PawnKindDef` defName (`Muffalo`, `Colonist`, `Pirate`). `faction` is `wild` (default, no faction — animals/wild men), `player` (your colony), or `hostile` (a deterministic enemy faction). `gender` is `male`/`female` (omit for random). `hediffs` applies health conditions: `"Flu:0.4; MissingBodyPart@Leg; BionicArm@Arm"` — each is a `HediffDef`, optionally `@BodyPartDef` to target a part and/or `:severity`; an unknown def or a part the race lacks fails the step before any pawn spawns. `count` defaults to 1, `spacing` to 2. `clear` (default false) bulldozes the spawn cells first. Any cell that still can't take a pawn is reported, not silently skipped. |

**Composite**

| Type | Args | Notes |
|---|---|---|
| `Timelapse` | `fromHour`, `toHour` **or** `steps`, `stepHours`, `fileNamePrefix`, `settleFrames`, `fps` | Desugared at load time into a `SetTime` (first frame only) or `AdvanceTime` / `Wait` / `Screenshot` triple per frame, so nothing downstream needs to know it exists. Defaults: `0` → `24` exclusive, `1`-hour steps, prefix `timelapse`, `settleFrames` 2, `fps` 12. The runner stitches the frames into an mp4. **The range may wrap past midnight** — `toHour` below `fromHour` (`16` → `4`) means "through to 04:00 next morning", 12 hours, hours taken modulo 24. That is one continuous frame sequence and one video, which is why it beats two steps for a dusk-to-moonrise sweep. **`steps` bounds the sweep by frame count instead of end hour** — `fromHour 12, stepHours 0.25, steps 96` is a full day starting at noon. Prefer it for anything watched as a LOOP: frame count is what a gif's byte budget is actually made of, and starting at noon puts the loop's seam where shadows are shortest instead of where they are longest and most directional. Giving both `steps` and `toHour` is rejected, as is `fromHour == toHour` without `steps`. |

| `TickLapse` | `ticks`, `steps`, `fileNamePrefix`, `settleFrames`, `fps` | The same sweep measured in TICKS: desugared into an `AdvanceTicks` / `Wait` / `Screenshot` triple per frame, every frame identical (no special first frame — where the clock starts is the scenario's business). Defaults: `ticks` 10, `steps` 120, prefix `ticklapse`, `settleFrames` 2, `fps` 20 — a 20-game-minute span played back as a 6-second clip. Same 512-frame cap and the same mp4 stitch as `Timelapse`. **Use it for anything that animates on the tick counter rather than on the hour** — an aurora curtain, a weather panner. `Timelapse` films the hours changing; `TickLapse` holds the hour still and films one thing moving inside it. Not a substitute for one another: a watchable aurora interval is ~10 ticks, i.e. `stepHours` `0.004`. |

A timelapse is a **clock sweep, not a screen recording** — each frame is a still capture after the
clock is jumped and the render is allowed to settle, which is why `settleFrames` exists. A tick lapse
is the same thing at the other end of the scale: still captures, exact jumps, no recording. Both
deliberately use jumps rather than `FastForward`, whose ticks keep running through the settle frames
and the screenshot flush and would space the captures unevenly.

### Profiling: per-patch cost and call count

A probe times **one call** of a hot path. It never asks **how often that call happens**, and in
practice the call count is the more interesting number.

**Profiling is on by default and needs no scenario JSON.** Every run is launched with
[Dubs Performance Analyzer](https://steamcommunity.com/sharedfiles/filedetails/?id=2038874626)
in its mod list, the analyzer is started once after the first map load, and each scenario gets its
counters zeroed at its first step and harvested at its last. Every scenario in the report therefore
carries a per-patch cost table named `scenario`, and every report carries `Profiled: true`. Pass
`--no-profiler` to opt out; if the analyzer is not installed, the run says so and continues
un-profiled.

Profiling is a property of the **run**, not of a scenario, and that is what makes it free: one
analyzer started before the first scenario means every scenario is instrumented identically from
before any of them ran, so no scenario leaves a profiler behind for the next one and a suite still
costs one boot. (The earlier opt-in design left `ScenarioResidue.Profiler` behind, so a ten-scenario
profiling suite paid nine mid-suite reloads.)

The explicit `Profile` step is still there, for measuring **exactly this window** rather than the
whole scenario — and it is the only way to `ProfileAssert` on a number, since a run-level table does
not exist until the scenario has ended. It lets a scenario pin three things no probe can express:

- **calls per frame** — catches a hook that starts firing twice as often after a refactor. Two patches
  costing the same per call but firing 3,700 and 7,400 times a window are telling you which one is
  hooked per-map and which per-section.
- **worst frame** — an average of 0.45 ms with a `maxMsPerFrame` of 3.0 ms is a dropped frame the
  average hides completely. A rolling-refresh design exists to avoid exactly that spike, and the only
  way to know it worked is to measure the worst frame.
- **total cost across every patch** — a probe covering one of three patches leaves two thirds of the
  subsystem invisible.

```json
{ "type": "Profile",
  "args": { "name": "aurora", "prefix": "CelestialLighting", "frames": "600" } },

{ "type": "ProfileAssert",
  "args": { "table": "aurora", "label": "*", "metric": "avgMsPerFrame", "max": "1.0" } },

{ "type": "ProfileAssert",
  "args": { "table": "aurora", "label": "Patch_GameConditionManager",
            "metric": "callsPerFrame", "expectedValue": "6", "tolerance": "1" } }
```

| Type | Args | Notes |
|---|---|---|
| `Profile` | `name`, `prefix`, `frames`, `warmupFrames`, `timeSpeed`, `entry` | Composite: desugars into `ProfileStart` / `ProfileMeasure` / `ProfileStop`. `name` and `prefix` are **required**. `frames` defaults to 600 (max 1999 — the analyzer's ring buffer). `warmupFrames` defaults to 30. `timeSpeed` defaults to `normal`. |
| `ProfileStart` | `entry`, `timeSpeed`, `warmupFrames` | Activates and warms the analyzer. |
| `ProfileMeasure` | `frames` | Zeroes the counters and opens the window. `frames` defaults to **0**, meaning "don't idle — the steps that follow are the window". |
| `ProfileStop` | `name`, `prefix` | Harvests the table and stops profiling. |
| `ProfileAssert` | `table`, `label`, `metric`, `max` \| `min` \| `expectedValue`+`tolerance` | `label` defaults to `*` (the table's totals). |

Writing the three primitives out instead of `Profile` is how you profile *a span of steps* rather
than a span of idle frames — `ProfileStart`, `ProfileMeasure`, a `Timelapse`, `ProfileStop`.

**`prefix`** is matched against the analyzer's own row label, which is `Namespace.Type:Method(params)` for the
*patch* method — so a mod's root namespace is the prefix you want. It is required, case-sensitive and
anchored at the start. A prefix that matches nothing **fails the step** rather than recording an empty
table, because an empty table is indistinguishable from "this mod costs nothing".

**Metrics**: `avgMsPerFrame`, `maxMsPerFrame`, `totalMs`, `calls`, `callsPerFrame`,
`maxCallsPerFrame`, `avgUsPerCall`, `percentOfFrame`, `percentOfSixtyFpsBudget`. `label` may be an
exact row label, a unique substring of one (ambiguity is an error, not a guess), or `*` for the
table's totals — which is the assertion that survives a patch being renamed or split in two. Prefer a
substring: the full label carries a parameter list
(`RimWorldTestHarness.Mod.Patch_ForcedLatitude:Postfix(ref Vector2&)`), and pinning that in a scenario
breaks on any signature change.

#### Reading the numbers without fooling yourself

- **The percentage column is a trap.** The analyzer reports a share of the frame the machine *actually
  achieved*, and a headless harness run happily hits 350 fps. A row at 15.9% of a 2.85 ms frame is
  ~2.7% of a 60 fps budget. Every table therefore records `frameMs` and `framesPerSecond` next to the
  percentages, and every row carries `percentOfSixtyFpsBudget` as well as `percentOfFrame`. Compare
  the second one before concluding anything is expensive.
- **Call counts include calls that do nothing.** A patch guarding `if (map.x != y) return;` still
  counts every entry, so `avgUsPerCall` averages the expensive invocations together with the trivial
  ones and *understates* the real cost of the ones that do work. `maxCallsPerFrame` is the only shape
  information available behind that mean.
- **Absolute figures carry the profiler's own overhead.** Trust ratios between rows, and changes in
  the same row between builds. These caveats also ride in every table's `Notes`, because the moment
  someone misreads them is while looking at a report, not while reading this file.

#### A run that measured nothing must never look like one that measured zero

A profiled run does not always have anything to measure, and a table of zeroes is the worst possible
way to say so — it looks like a measurement, means "nothing was measured", and reads as "this mod is
free". So the harness never writes one. Instead the scenario's report carries a `ProfileSkipReason`,
which `run_test.sh` prints as `PROFILING SKIPPED: …`, for each of:

- **the analyzer is not installed**, or is installed and RimWorld did not load it (different problems,
  different fixes, different messages — the runner scanned the disk, so it is the one that says which);
- **the run never became interactive** — no map had loaded when it finished. A run that verifies XML
  patch behaviour and never reaches a map is a normal way to use the harness, and it gets a reason
  rather than a table;
- **the scenario ran no steps**, so no window was opened;
- **no frames elapsed**, or **fewer than 30 did** — a per-frame mean over a three-frame scenario is one
  frame's noise wearing an average's clothes. Add a `Wait`, or use an explicit `Profile` step;
- **nothing instrumented ran** during the scenario.

The scenario still runs, still asserts and still gates `Pass` as normal. Only its cost table is absent.

Two more things a run-level table discloses rather than hides. Run-level profiling deliberately does
**not** force a game speed — it must not change what the scenarios it wraps around do — so a scenario
that jumped the clock leaves the colony paused, every tick-driven patch reads as free, and the table
records `PausedFrames` plus a note saying so. (An explicit `Profile` step forces `timeSpeed: normal`
instead, which is the right answer when you control the window.) And a run-level table has no `prefix`
filter, so it is capped at the 200 most expensive rows — with `Totals` computed over *all* matched rows
before the cap, and a note when rows were dropped.

#### The guardrail: `Profiled`, and `pinnedUnder`

Profiling rewrites the body of every Harmony-patched method in the load, so every timing number in a
profiled run — ordinary `Probe` steps included, not just profile tables — is measured through an
instrumented build. The failure this creates: pin a probe's `expectedValue` from a profiled run,
compare it against an unprofiled one, and the check moves for a reason that has nothing to do with the
code under test. Now that profiling is the default, "pinned before this change, checked today" is that
mismatch by default.

Every report therefore carries `Profiled: true|false`, and `run_test.sh` prints it next to `pass=`.
That is the weak half of the mitigation — it relies on someone noticing a line of output. The strong
half is that a `Probe` step can record **which mode its own number was pinned under**, and a mismatch
is an error that fails the scenario:

```json
{ "type": "Probe",
  "args": { "probeName": "aurora_curtain_cost", "expectedValue": "0.42", "tolerance": "0.05",
            "pinnedUnder": "no-profiler" } }
```

`pinnedUnder` is `any` (default), `profiler` or `no-profiler`. The default is `any` because most probes
read state — a season, a latitude, a glow level — and gating those on the profiler's presence would be
noise that teaches people to switch the gate off. Put it on **timing** probes. A misspelling fails at
load rather than silently parsing as `any`, because a guardrail you believe you have and don't is worse
than none.

Pin timing baselines with `--no-profiler`, and declare `pinnedUnder: "no-profiler"` so the run that
would have compared them across modes fails instead of drifting.

#### Running it

`Scenarios/profile_harness_patches.json` profiles the harness's own patches with an explicit window
and asserts on it; it needs no mod under test:

```bash
./Runner/run_test.sh Scenarios/profile_harness_patches.json      # profiled: the default
./Runner/run_test.sh --no-profiler Scenarios/spawn_pawns.json    # not instrumented
./Runner/run_test.sh --profiler Scenarios/spawn_pawns.json       # fail if not installed
```

`--profiler` differs from the default in exactly one way: a missing analyzer becomes a hard failure
instead of a warning, because you asked for it by name.

Without the analyzer, an explicit `Profile` step **skips the scenario** — the same path `LandInOrbit`
takes without Odyssey. The run stays green, and the report, `Player.log` and the runner's summary all
say `SKIPPED` with the reason. Put `Profile` steps in their **own scenario** for the same reason the
skip is scenario-wide.

A live run of it, for calibration — and for what the extra columns buy you:

| Label | Avg ms/frame | Max ms/frame | Calls | Calls/frame | **Max calls/frame** | µs/call |
|---|---|---|---|---|---|---|
| `Patch_ForcedLatitude:Postfix` | 0.0046 | 0.100 | 23,081 | 76.7 | **1,496** | 0.06 |
| `Patch_DriveScenario:Postfix` | 0.0009 | 0.0017 | 300 | 1.0 | 1 | 0.92 |

Two patches, three orders of magnitude apart in call count, and one of them with a frame that took
1,496 calls against a mean of 77. Nothing in a per-call timing probe would have shown either.

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
| `--mod-overlay <folder>` | Install `<folder>/1.6/Assemblies` over the already-installed mod with the same `packageId` for the run, then restore it. How you test a git worktree. It swaps an **activated** mod's assemblies; it does not activate one, so the installed copy still needs its own `--mod`. |
| `--install <src>:<dst>` | The same overlay with both paths spelled out, for anything that isn't a mod's assemblies. |
| `--suite <list.txt>` | Run the scenarios named in a list file. |
| `--isolation=auto\|always\|never` | How hard a suite works to isolate one scenario from the next. |
| `--no-teardown` | Leave symlinks / `ModsConfig` / `autostart.rws` in place for post-mortem debugging. |
| `--delete-frames` | Delete timelapse PNGs once stitched into video. |
| `--without-dlc <packageId>` | Leave an installed DLC out of this run's `ModsConfig` (e.g. `ludeon.rimworld.odyssey`). Repeatable. For exercising a scenario's skip-without-the-DLC path on a machine that owns the DLC — otherwise that branch is code nobody can run. |
| `--profiler` | Activate Dubs Performance Analyzer (Workshop 2038874626) for this run. **On by default**; passing it explicitly only changes one thing — a missing analyzer becomes a hard failure instead of a warning, because you asked for it by name. |
| `--no-profiler` | Leave the analyzer out. The flag to pin timing baselines under: the analyzer instruments every Harmony patch in the load, so every timing number in a profiled run, probes included, comes from an instrumented build. Record the mode on the `Probe` step (`pinnedUnder`) so a cross-mode comparison fails the run instead of drifting. |
| `--print-config` | Print every path the run would use and exit — touching, locking, and creating nothing. |

`--without-dlc` deactivates rather than uninstalls, which is the right lever: `ModsConfig.<Dlc>Active`
reads the active-mod list, not the disk. Note that a **fixture save made with the DLC cannot be
loaded without it** — that's vanilla, and it will crash the game rather than skip — so pair
`--without-dlc` with a `-quicktest` scenario (empty `saveFile`) or a fixture made without it.

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

Every scenario also carries `Profiled` — whether the analyzer was instrumenting the load while it ran
— and either a `Profiles` array or a `ProfileSkipReason` saying why there isn't one. A profiled run
writes one `scenario` table per scenario automatically, plus one per explicit `Profile`/`ProfileStop`
step:

```json
"Profiled": true,
"Profiles": [
  { "Name": "aurora", "Prefix": "CelestialLighting",
    "MeasuredFrames": 600, "SampledFrames": 601, "PausedFrames": 0,
    "FrameMs": 2.847, "FramesPerSecond": 351.25,
    "RowsBeforeFilter": 812, "RowsMatched": 3,
    "Totals": { "Label": "*", "AvgMsPerFrame": 0.502, "Calls": 14904,
                "PercentOfSixtyFpsBudget": 3.012 },
    "Rows": [
      { "Label": "CelestialLighting.Patch_GameConditionManager:Postfix",
        "AvgMsPerFrame": 0.453, "MaxMsPerFrame": 2.999, "Calls": 3738,
        "CallsPerFrame": 6.23, "AvgUsPerCall": 72.71,
        "PercentOfFrame": 15.911, "PercentOfSixtyFpsBudget": 2.718 }
    ],
    "Notes": ["Calls counts every entry into the method, including …"] }
]
```

`Totals` is the whole-table pseudo-row a `ProfileAssert` with `label: "*"` reads. It is stored rather
than derived from `Rows` because `Rows` is capped: totals recomputed from a truncated list would report
a subsystem getting cheaper because the report got long.

Tables are **informational** — only a `ProfileAssert` step turns one of these numbers into something
that gates `Pass`. The primary use is diffing the same table between two builds, and a run going red
because the machine was busy would train everyone to ignore the colour. A `ProfileAssert` records an
ordinary `ProbeCheckResult` with a `Comparison` of `within`, `atMost` or `atLeast`. It can only target
a table an explicit `Profile`/`ProfileStop` step produced — a run-level table does not exist until
after the scenario's last step has run.

A suite wraps one unchanged `ScenarioReport` per scenario in a `SuiteReport` with `Pass`, `Errors`,
and `IsolationNotes`; you tell the two apart by the presence of a `Scenarios` key. The single-scenario
shape is byte-identical to what it always was, so existing consumers keep working.

The gate is stricter than "all probes passed", on purpose. A run fails if any probe check fails, **or
if there were any errors at all** — because a scenario whose every step errored has zero probe checks,
and "nothing was verified" otherwise reads exactly like "everything passed". Same reasoning at the
suite level: an empty suite fails, and scenarios a mid-suite abort never reached are listed with an
explicit "did not run" error rather than quietly omitted. Screenshots never affect `Pass`; they're a
complementary review channel, not a gate.

### Skipped scenarios

A step can declare a scenario **inapplicable to this install** rather than broken — today only
`LandInOrbit` without Odyssey. When it does, the rest of that scenario is abandoned, the report gains
`"Skipped": true` and a `SkipReason`, and `Pass` is computed as usual (so with nothing else wrong it
stays `true` and the run exits 0). A box without a paid DLC should not go red over something no edit
to any mod can fix.

That is the one case where a green result genuinely means less than it looks like, so the only
mitigation is volume: `Player.log` prints `SKIPPED: <reason>` on the scenario's finish line,
`run_test.sh` prints it under the scenario and again as a suite-level `skipped N/M scenario(s), which
verified NOTHING` summary. Anything recorded *before* the skip — an errored earlier step, a failed
probe — still gates `Pass` as normal. Skipping stops a scenario; it does not absolve it.

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
- **An overlaid mod still needs to be activated.** Testing a worktree takes both flags: `--mod` on the
  installed copy to put it in this run's `ModsConfig.xml`, and `--mod-overlay` on the worktree to swap
  in its build. With only the overlay the run looks healthy — the overlay resolves against `Mods/` and
  reports installing — while the mod never loads and every probe it registers is absent, which reads
  as a build problem rather than a missing flag. `--print-config` shows both halves without launching.
- **`override` methods are the classic silent breakage.** If RimWorld renames a base method, your
  override compiles fine and is simply never called. That's what `Tests/RimWorldTestHarness.ApiTests`
  is for — run `./test.sh` after every RimWorld update, before you load the game.
- **A failing step is never silently dropped.** Bad steps stay in the list and fail again at
  execution, and loader errors travel into the report, because a scenario that quietly ran fewer steps
  would verify less than it claims to. Nearly every design decision in this repo comes back to that
  one rule: a green run must never mean less than it looks like.

Further reading: `DESIGN.md` (architecture and the reasoning behind each decision), `TODO.md`
(what's left), `Runner/README.md` (runner specifics), `Fixtures/README.md` (fixture creation).
