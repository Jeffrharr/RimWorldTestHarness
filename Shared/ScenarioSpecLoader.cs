using System.IO;
using System.Text.Json;

namespace RimWorldTestHarness.Shared;

public static class ScenarioSpecLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ScenarioSpec LoadFromFile(string path)
    {
        string json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static ScenarioSpec LoadFromJson(string json)
    {
        ScenarioSpec spec = JsonSerializer.Deserialize<ScenarioSpec>(json, Options) ?? new ScenarioSpec();
        // Composite steps are desugared here, at the one choke point every consumer goes through,
        // so the drivers only ever see primitive steps and need no knowledge of them. The loaded
        // spec therefore no longer round-trips to its source JSON — nothing re-serializes a spec,
        // and the report records what actually ran.
        spec.Steps = TimelapseExpander.ExpandAll(spec.Steps, spec.LoadErrors);
        return spec;
    }
}
