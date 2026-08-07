using System.Collections.Generic;
using System.Linq;

namespace RimWorldTestHarness.Shared;

// Renders "what the scenario declared between these two frames" as text, for DeltaAssert.Inputs.
//
// WHY IT IS WORTH A FILE. A delta assertion that records only its verdict leaves the same gap that
// once hid a real defect for a whole PR cycle: a scenario recorded one of the two values that
// determined its result, so two runs with identical recorded numbers rendered eightfold differently
// and the report had nothing to point at. The fix is not more probes — it is that the comparison
// carries its own inputs. See DeltaAssert.Inputs.
//
// Pure, and separate from the step that uses it, for the usual reason: this is the part with real
// branching to get wrong (which steps count, what happens when a name matches nothing, what a
// desugared Timelapse looks like from here), and it is worth having offline tests over.
public static class DeltaInputs
{
    // Steps that describe plumbing rather than an input. A Wait exists so a frame can settle and a
    // Screenshot is the thing being compared; listing either tells a reader nothing about why the
    // two frames differ, and a settle-heavy scenario would bury the lines that do.
    private static readonly HashSet<string> Plumbing = new HashSet<string>
    {
        StepArgs.WaitType,
        StepArgs.ScreenshotType,
    };

    // Beyond this, the list stops being something a reader scans and becomes something they skip. A
    // delta spanning hundreds of steps is a scenario that should be asserting on a narrower pair
    // anyway, and the count in the trailing line says so out loud.
    public const int MaxDescribed = 20;

    // Long arg values (a vision rubric, a cells list) are elided rather than dropped: the reader
    // needs to know the arg was set, not to re-read its whole body here.
    public const int MaxValueLength = 60;

    public static List<string> Between(
        IReadOnlyList<ScenarioStep> steps, string baselineName, string targetName)
    {
        int from = IndexOfScreenshot(steps, baselineName, 0);
        if (from < 0)
            return new List<string> { $"(no Screenshot step named '{baselineName}' in this scenario)" };

        int to = IndexOfScreenshot(steps, targetName, from + 1);
        if (to < 0)
            return new List<string> { $"(no Screenshot step named '{targetName}' after '{baselineName}')" };

        List<string> described = steps
            .Skip(from + 1)
            .Take(to - from - 1)
            .Where(s => !Plumbing.Contains(s.Type))
            .Select(Describe)
            .ToList();

        // Said explicitly rather than left as an empty list, because "nothing was declared between
        // these frames" is a real and interesting answer — it means any measured difference came
        // from somewhere the scenario did not ask for.
        if (described.Count == 0)
            return new List<string> { "(no steps declared between the two frames)" };

        if (described.Count <= MaxDescribed)
            return described;

        List<string> capped = described.Take(MaxDescribed).ToList();
        capped.Add($"... and {described.Count - MaxDescribed} more step(s)");
        return capped;
    }

    private static int IndexOfScreenshot(IReadOnlyList<ScenarioStep> steps, string fileName, int start)
    {
        for (int i = start; i < steps.Count; i++)
        {
            if (IsScreenshotNamed(steps[i], fileName))
                return i;
        }

        return -1;
    }

    private static bool IsScreenshotNamed(ScenarioStep step, string fileName) =>
        step.Type == StepArgs.ScreenshotType
        && step.Args.TryGetValue(StepArgs.ScreenshotFileName, out string? name)
        && name == fileName;

    // "SetFeature(enabled=true, featureName=purpleLight)". Args are ordered by key so the same
    // scenario renders the same way every run — a report anyone diffs between builds must not move
    // because a dictionary enumerated differently.
    private static string Describe(ScenarioStep step)
    {
        IEnumerable<string> args = step.Args
            .OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={Elide(pair.Value)}");

        return $"{step.Type}({string.Join(", ", args)})";
    }

    private static string Elide(string value) =>
        value.Length <= MaxValueLength ? value : value.Substring(0, MaxValueLength) + "...";
}
