using System.Collections.Generic;
using System.Linq;

namespace RimWorldTestHarness.Shared.Steps.BuiltIn;

// Declares a rubric for an LLM judge over screenshots this scenario already captured, plus the
// game's recent warnings/errors. The third verification tier, alongside Probe (numeric, hard gate)
// and bare Screenshot (visual, ungated).
//
// Written through the step registry like any contributor's step would be — which is why #10 landed
// first. Nothing central needed editing to add it.
public sealed class AssertStep : IStepSpec
{
    public const string StepType = "Assert";

    // "vision" today. "delta" (an automated pixel diff, no LLM) is the other tier issue #7 proposes;
    // the arg exists now so scenarios written today don't need rewriting when it lands.
    public const string KindArg = "kind";
    public const string VisionKind = "vision";
    public const string DeltaKind = "delta";

    public const string ImagesArg = "images";            // comma-separated fileNames from earlier Screenshot steps
    public const string PromptArg = "prompt";            // the rubric
    public const string ExpectArg = "expect";            // one-line expected outcome
    public const string ConfidenceGateArg = "confidenceGate";  // float 0..1, default 0.7
    public const string IdArg = "id";                    // optional; defaults to the step index
    public const string LogLinesArg = "logLines";        // int >= 0, default 25; 0 disables log capture

    public const int DefaultLogLines = 25;

    public string Type => StepType;

    // Read-only: it records a rubric and copies some log lines. It changes nothing about the world,
    // which is what lets a suite put an Assert anywhere without paying for an isolation reload.
    public ScenarioResidue Residue => ScenarioResidue.None;

    // Not live-callable. An Assert's whole output is an entry in a scenario report, and the
    // companion channel has no report to write into — it answers one command at a time against a
    // running game. Exposing it there would produce a verb that silently does nothing useful.
    public bool LiveCallable => false;

    public bool TryValidate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!TryValidateKind(args, out error))
            return false;

        if (!TryValidateImages(args, out error))
            return false;

        if (!args.TryGetValue(PromptArg, out string? prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            error = $"'{PromptArg}' is required — it is the rubric the judge answers against";
            return false;
        }

        return TryValidateConfidenceGate(args, out error) && TryValidateLogLines(args, out error);
    }

    private static bool TryValidateKind(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(KindArg, out string? kind) || string.IsNullOrWhiteSpace(kind))
        {
            error = $"'{KindArg}' is required (currently only '{VisionKind}')";
            return false;
        }

        // A 'delta' assert is rejected with its own message rather than a generic one. Silently
        // accepting a kind nothing evaluates would produce a scenario that looks like it asserts
        // something and does not — the exact shape of failure this tier exists to catch.
        if (kind == DeltaKind)
        {
            error = $"'{DeltaKind}' asserts are not implemented yet (issue #7) — this step would " +
                    "verify nothing, so it is rejected rather than silently skipped";
            return false;
        }

        if (kind != VisionKind)
        {
            error = $"unknown {KindArg} '{kind}' (expected '{VisionKind}')";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateImages(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(ImagesArg, out string? images) || string.IsNullOrWhiteSpace(images))
        {
            error = $"'{ImagesArg}' is required (comma-separated fileNames from earlier Screenshot steps)";
            return false;
        }

        if (ParseImages(images).Count == 0)
        {
            error = $"'{ImagesArg}' listed no usable file names (got '{images}')";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateConfidenceGate(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(ConfidenceGateArg, out string? raw))
        {
            error = null;
            return true;
        }

        if (!float.TryParse(raw, out float gate) || gate < 0f || gate > 1f)
        {
            error = $"'{ConfidenceGateArg}' must be a number between 0 and 1 (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateLogLines(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!args.TryGetValue(LogLinesArg, out string? raw))
        {
            error = null;
            return true;
        }

        if (!int.TryParse(raw, out int lines) || lines < 0)
        {
            error = $"'{LogLinesArg}' must be a whole number >= 0 (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    // Shared with the executing half so the names it resolves are exactly the ones validated here.
    public static List<string> ParseImages(string raw) =>
        raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    public static float ReadConfidenceGate(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(ConfidenceGateArg, out string? raw) && float.TryParse(raw, out float gate)
            ? gate
            : VisionAssert.DefaultConfidenceGate;

    public static int ReadLogLines(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(LogLinesArg, out string? raw) && int.TryParse(raw, out int lines)
            ? lines
            : DefaultLogLines;
}
