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
        ScenarioSpec? spec = JsonSerializer.Deserialize<ScenarioSpec>(json, Options);
        return spec ?? new ScenarioSpec();
    }
}
