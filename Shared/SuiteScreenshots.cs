using System.Collections.Generic;
using System.Text;

namespace RimWorldTestHarness.Shared;

// Screenshot filename policy for a suite. Two scenarios written independently will happily both ask
// for "shot.png" or for a Timelapse with fileNamePrefix "timelapse", and in one shared report folder
// the second silently overwrites the first. That failure mode is the worst kind for this harness: the
// run stays green, the probe numbers are real, and the images being reviewed belong to the wrong
// scenario.
//
// So two independent defences, both pure and offline-tested:
//
//   1. Qualify every suite screenshot name with its scenario, so collisions between independently
//      authored scenarios don't arise in the first place.
//   2. Check the FINAL names for duplicates anyway and fail the suite if any remain — that catches
//      the residual cases qualification can't (two scenarios whose names sanitize to the same prefix,
//      or one scenario using the same fileName twice).
//
// Runner/run_test.sh mirrors Qualify in bash to find a suite's timelapse frames for stitching; the
// rule is deliberately trivial (sanitize, join with "__") so the two can't drift in interesting ways,
// and QualifyPrefixIsPinned in the tests locks the exact spelling. Changing either side means
// changing both.
public static class SuiteScreenshots
{
    // Double underscore rather than '-' or '_': scenario names and file names both routinely contain
    // single underscores and hyphens, so a single-character separator would be ambiguous when reading
    // a filename back to work out which scenario produced it.
    public const string Separator = "__";

    // Filename-safe form of a scenario name. Conservative on purpose: the timelapse frame glob in
    // Runner/run_test.sh and ffmpeg's %04d pattern both go through the shell, and the harness has no
    // business emitting a filename that needs quoting.
    public static string PrefixFor(string scenarioName)
    {
        StringBuilder sb = new StringBuilder(scenarioName.Length);
        for (int i = 0; i < scenarioName.Length; i++)
            sb.Append(IsSafe(scenarioName[i]) ? scenarioName[i] : '_');

        string prefix = sb.ToString();
        // A name made entirely of unsafe characters (or an empty one) would otherwise produce a
        // prefix of just underscores, or nothing at all; the collision check below then catches any
        // two such scenarios rather than letting them share a folder.
        return prefix.Length == 0 ? "scenario" : prefix;
    }

    public static string Qualify(string scenarioName, string fileName) =>
        PrefixFor(scenarioName) + Separator + fileName;

    private static bool IsSafe(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
        c == '.' || c == '-' || c == '_';

    // Every file name the scenario's Screenshot steps will write. Timelapse is already desugared into
    // per-frame Screenshot steps by the time a spec reaches here (ScenarioSpecLoader), so this sees
    // the real, numbered frame names rather than having to re-derive them from the sweep args.
    public static List<string> FileNamesOf(ScenarioSpec scenario)
    {
        List<string> names = new List<string>();
        for (int i = 0; i < scenario.Steps.Count; i++)
            AddFileName(scenario.Steps[i], names);

        return names;
    }

    private static void AddFileName(ScenarioStep step, List<string> names)
    {
        if (step.Type != StepArgs.ScreenshotType)
            return;
        if (!step.Args.TryGetValue(StepArgs.ScreenshotFileName, out string? fileName) || fileName == null)
            return;

        names.Add(fileName);
    }

    // Any two scenarios (or any one scenario) that would write the same file. Reported as suite-level
    // errors, which fail the suite via ReportComparer.AllPass — the run still executes so its other
    // artifacts survive, but it can never come back green over overwritten images.
    public static List<string> CollisionErrors(IReadOnlyList<ScenarioSpec> scenarios)
    {
        Dictionary<string, string> owners = new Dictionary<string, string>();
        List<string> errors = new List<string>();

        for (int i = 0; i < scenarios.Count; i++)
            AddCollisions(scenarios[i], owners, errors);

        return errors;
    }

    private static void AddCollisions(ScenarioSpec scenario, Dictionary<string, string> owners, List<string> errors)
    {
        List<string> fileNames = FileNamesOf(scenario);
        for (int i = 0; i < fileNames.Count; i++)
            AddCollision(scenario.Name, fileNames[i], owners, errors);
    }

    private static void AddCollision(string scenarioName, string fileName,
                                     Dictionary<string, string> owners, List<string> errors)
    {
        string qualified = Qualify(scenarioName, fileName);
        if (!owners.TryGetValue(qualified, out string? firstOwner))
        {
            owners[qualified] = scenarioName;
            return;
        }

        errors.Add(
            $"screenshot name collision: '{qualified}' would be written by both '{firstOwner}' and " +
            $"'{scenarioName}' — rename one scenario or one fileName/fileNamePrefix");
    }
}
