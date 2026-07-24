# Fixtures

Save files (`.rws`) referenced by `Scenarios/*.json`'s `saveFile` field. Gitignored — RimWorld
saves are binary XML blobs tied to a specific mod list, so they don't diff usefully and aren't
worth tracking in git.

**Not auto-creatable by Claude.** There's no headless "new colony" API to script from outside the
game (`Verse.QuickStarter`/`-quicktest` only loads the Play scene, it doesn't generate a save —
see the top-level `DESIGN.md`). Each fixture has to be created once, manually, in-game:

1. Launch RimWorld with only this harness's dependencies active (Core + `brrainz.harmony`).
2. Start a new colony on **any tile** — landing latitude doesn't matter. `SetTile` scenario steps
   override latitude at runtime via a Harmony patch on `WorldGrid.LongLatOf`, so the fixture never
   needs to match a scenario's latitude band; one fixture can be reused across scenarios that only
   differ in `SetTile`/`SetSeason`/`SetTime` args.
3. Get it to a stable, saveable state — a few colonists, no crashes, dev mode enabled.
4. Save it into this folder under the name the scenario's `saveFile` expects.

Keep fixtures minimal (small map, few pawns, no complex mod content) so loads stay fast and the
save stays easy to regenerate if it ever needs updating for a new RimWorld version.

## Fixtures needed so far

- `minimal_colony.rws` — referenced by `Scenarios/shadow_lean_equinox.json`. Not yet created.
