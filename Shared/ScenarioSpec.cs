using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RimWorldTestHarness.Shared;

// A scenario is the "spec" half of spec-driven verification: a declarative list of steps the
// in-game Mod replays against a live Map, plus (optionally) the expected probe values a numeric
// gate checks afterward. Screenshot steps exist alongside probe steps in the same scenario so a
// single run can produce both a pass/fail gate AND images for a human/Claude visual check —
// neither mode is exclusive.
public sealed class ScenarioSpec
{
    public string Name { get; set; } = "";

    // Relative to Fixtures/. Not auto-creatable — see Fixtures/README.md.
    public string SaveFile { get; set; } = "";

    // packageId -> Steam Workshop file id, for real Workshop mods (beyond this harness itself
    // and its brrainz.harmony dependency) this scenario's save/steps depend on. Single source of
    // truth for both Runner/fetch_mods.sh (downloads anything missing) and the ModsConfig-writing
    // step in Runner/run_test.sh (still a TODO there) — keeping one list here avoids the two
    // drifting out of sync with each other.
    public Dictionary<string, string> RequiredMods { get; set; } = new();

    public List<ScenarioStep> Steps { get; set; } = new();

    // Problems the loader found while desugaring composite steps (see TimelapseExpander). Not part
    // of the file format — it's loader output, hence JsonIgnore — but it has to travel with the
    // spec so ScenarioDriver can fold it into the run's report. A scenario that silently dropped a
    // malformed step would be worse than one that fails: the run would go green having verified
    // less than it claimed.
    [JsonIgnore]
    public List<string> LoadErrors { get; set; } = new();
}

// Deliberately a flexible string/string arg bag rather than a typed union: RimWorld's own dev
// actions are heterogeneous (set tile, set season, fast-forward N ticks, read a probe, capture a
// screenshot), new step types will keep getting added as more mods get harness coverage, and a
// JSON-driven spec format needs to stay easy to hand-author without regenerating C# types for
// every new step kind. ScenarioSpecLoader validates known Type values; StepArgs.cs documents the
// expected Args keys per Type.
public sealed class ScenarioStep
{
    public string Type { get; set; } = "";
    public Dictionary<string, string> Args { get; set; } = new();
}
