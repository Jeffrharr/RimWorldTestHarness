# Evidence: `Scenarios/orbital_platform.json`

Two frames captured by a real run of that scenario, kept because `Runner/reports/` is gitignored and
a PR needs somewhere to point at. They are the *shape* half of this repo's verification split: probes
and `Player.log` lines carry the numbers, images carry what a number cannot say — that the map does
not look like a planet.

Captured on RimWorld 1.6.4871 rev600 with Odyssey active, from `Fixtures/minimal_colony.rws`
(30% planet coverage), `LandInOrbit` at latitude 45, `mapSize` 150.

| File | What it shows |
|---|---|
| `orbit_noon.png` | Local noon, day 15. Bare hull, planet's day side around it, nine steel wall blocks casting directional shadows. |
| `orbit_midnight.png` | Local midnight, same tile and season. Planet's night side; the shadows are gone with the light. |

**They are not a controlled A/B.** The noon capture carries alert text and the Learning-helper panel;
the midnight one has no UI overlay. They demonstrate the harness reaching a real orbital map at two
times of day — not a before/after of any mod effect. Anything wanting a matched pair should capture
one, with `SetFeature` off/on in a single boot (see the README's "Making your mod testable").

The run's own postconditions are what actually prove the map is orbital — layer, `inVacuum` biome and
per-cell `GetVacuum` are checked by `LandInOrbit` and fail the step if wrong. These images corroborate
that; they do not stand in for it. Incidentally, the noon frame's Learning-helper panel lists an
`Orbit` entry, which is vanilla's tutor reacting to the layer independently of anything we assert.

Not regenerated automatically. If the generation path changes enough that these stop being
representative, re-run the scenario and replace them in the same commit as the change.
