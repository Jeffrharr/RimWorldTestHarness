using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RimWorldTestHarness.Shared;

// An ordered set of scenarios to run inside one game load, as loaded from a suite list file.
public sealed class SuiteSpec
{
    public List<ScenarioSpec> Scenarios { get; set; } = new();

    // Problems reading the list itself, or reading one of the scenarios it names. Folded into the
    // run's SuiteReport.Errors so they gate Pass.
    public List<string> LoadErrors { get; set; } = new();
}

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

    // Loads every scenario a suite list file names. A scenario that fails to load is NOT dropped: it
    // becomes a named, step-less placeholder carrying the reason in its own LoadErrors, so it still
    // appears in the suite report as a failing scenario. Dropping it would shrink the suite silently,
    // which is the failure mode this whole file's validation exists to avoid.
    public static SuiteSpec LoadSuiteFromFile(string listPath)
    {
        SuiteSpec suite = new SuiteSpec();
        string text = File.ReadAllText(listPath);
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(listPath)) ?? "";

        List<string> paths = SuiteList.Parse(text, baseDir, suite.LoadErrors);
        for (int i = 0; i < paths.Count; i++)
            suite.Scenarios.Add(LoadOrPlaceholder(paths[i], suite.LoadErrors));

        return suite;
    }

    private static ScenarioSpec LoadOrPlaceholder(string path, List<string> suiteErrors)
    {
        try
        {
            return LoadFromFile(path);
        }
        catch (Exception ex)
        {
            string reason = $"could not load scenario '{path}': {ex.Message}";
            suiteErrors.Add(reason);
            // Named after the file rather than left blank, so the report line points at something the
            // reader can act on even though the spec's own Name never got parsed.
            ScenarioSpec placeholder = new ScenarioSpec { Name = Path.GetFileNameWithoutExtension(path) };
            placeholder.LoadErrors.Add(reason);
            return placeholder;
        }
    }

    public static ScenarioSpec LoadFromJson(string json)
    {
        ScenarioSpec spec = JsonSerializer.Deserialize<ScenarioSpec>(json, Options) ?? new ScenarioSpec();
        // Composite steps are desugared here, at the one choke point every consumer goes through,
        // so the drivers only ever see primitive steps and need no knowledge of them. The loaded
        // spec therefore no longer round-trips to its source JSON — nothing re-serializes a spec,
        // and the report records what actually ran.
        spec.Steps = Steps.StepExpansion.ExpandAll(spec.Steps, spec.LoadErrors);
        // Validated after expansion so it checks the primitive steps that will actually execute, and
        // so a Timelapse that failed to expand isn't reported a second time.
        StepValidator.ValidateAll(spec.Steps, spec.LoadErrors);
        return spec;
    }
}
