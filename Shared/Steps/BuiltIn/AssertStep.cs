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

    // The two automated-versus-judged halves of issue #7. They share a step type rather than getting
    // one each because they answer the same question — "did the effect reach the screen?" — over the
    // same evidence, and a scenario author choosing between them is choosing how strict an answer
    // they can afford, not what they are asking about.
    public const string KindArg = "kind";
    public const string VisionKind = "vision";
    public const string DeltaKind = "delta";

    public const string ImagesArg = "images";            // vision: comma-separated fileNames from earlier Screenshot steps
    public const string PromptArg = "prompt";            // vision: the rubric
    public const string ExpectArg = "expect";            // one-line expected outcome (both kinds)
    public const string ConfidenceGateArg = "confidenceGate";  // vision: float 0..1, default 0.7
    public const string IdArg = "id";                    // optional; defaults to the step index
    public const string LogLinesArg = "logLines";        // vision: int >= 0, default 25; 0 disables log capture

    public const int DefaultLogLines = 25;

    // delta: the two frames, named by the fileName of the Screenshot steps that captured them.
    // Baseline first — every direction below is expressed as a movement FROM baseline TO target, and
    // a pair with no declared order would make "brighter" a coin flip.
    public const string BaselineArg = "baseline";
    public const string TargetArg = "target";

    public const string RegionArg = "region";            // delta: "full" (default) or "X,Y,W,H" in pixels
    public const string StrideArg = "stride";            // delta: int >= 1, default 2
    public const string DirectionArg = "direction";      // delta: see Directions
    public const string MinDeltaEArg = "minDeltaE";      // delta: float >= 0, median ΔE floor
    public const string MaxDeltaEArg = "maxDeltaE";      // delta: float >= 0, median ΔE ceiling

    public const string FullRegion = "full";
    public const string AnyDirection = "any";
    public const int DefaultStride = 2;

    // Mirrors Runner/delta_gate.py's DIRECTIONS. The gate is the thing that knows which measured
    // number each name reads; this list exists only so a typo fails at load time instead of after a
    // run has already spent its frames.
    public static readonly string[] Directions =
    {
        AnyDirection, "brighter", "darker", "warmer", "cooler", "purpler", "greener",
    };

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
        if (!TryReadKind(args, out string kind, out error))
            return false;

        return kind == DeltaKind
            ? TryValidateDelta(args, out error)
            : TryValidateVision(args, out error);
    }

    private static bool TryReadKind(
        IReadOnlyDictionary<string, string> args, out string kind, out string? error)
    {
        kind = args.TryGetValue(KindArg, out string? raw) ? raw : "";

        if (string.IsNullOrWhiteSpace(kind))
        {
            error = $"'{KindArg}' is required ('{VisionKind}' or '{DeltaKind}')";
            return false;
        }

        if (kind != VisionKind && kind != DeltaKind)
        {
            error = $"unknown {KindArg} '{kind}' (expected '{VisionKind}' or '{DeltaKind}')";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateVision(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!TryValidateImages(args, out error))
            return false;

        if (!args.TryGetValue(PromptArg, out string? prompt) || string.IsNullOrWhiteSpace(prompt))
        {
            error = $"'{PromptArg}' is required — it is the rubric the judge answers against";
            return false;
        }

        return TryValidateConfidenceGate(args, out error) && TryValidateLogLines(args, out error);
    }

    private static bool TryValidateDelta(IReadOnlyDictionary<string, string> args, out string? error)
    {
        if (!TryValidateFramePair(args, out error))
            return false;

        if (!TryValidateRegion(args, out error))
            return false;

        if (!TryValidateStride(args, out error))
            return false;

        if (!TryValidateDirection(args, out error))
            return false;

        if (!TryValidateBounds(args, out error))
            return false;

        return TryValidateAssertsSomething(args, out error);
    }

    private static bool TryValidateFramePair(IReadOnlyDictionary<string, string> args, out string? error)
    {
        foreach (string arg in new[] { BaselineArg, TargetArg })
        {
            if (!args.TryGetValue(arg, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                error = $"'{arg}' is required — it names the fileName of an earlier Screenshot step";
                return false;
            }
        }

        // Comparing a frame with itself is always a perfect zero, which would read as "no effect"
        // and is far more likely a copy-paste than a deliberate assertion.
        if (args[BaselineArg].Trim() == args[TargetArg].Trim())
        {
            error = $"'{BaselineArg}' and '{TargetArg}' name the same screenshot " +
                    $"('{args[BaselineArg]}') — a frame cannot differ from itself";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateRegion(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;

        if (!args.TryGetValue(RegionArg, out string? raw) || string.IsNullOrWhiteSpace(raw)
            || raw.Trim() == FullRegion)
        {
            return true;
        }

        // Shape only. Whether the rect fits inside the frame cannot be known until something has
        // decoded a PNG, so that check belongs to Runner/frame_delta.py's parse_region; this one is
        // here to catch the typo before a run spends its frames.
        string[] parts = raw.Split(',');
        if (parts.Length != 4 || parts.Any(p => !int.TryParse(p.Trim(), out _)))
        {
            error = $"'{RegionArg}' must be '{FullRegion}' or four whole pixels 'X,Y,W,H' (got '{raw}')";
            return false;
        }

        if (int.Parse(parts[2].Trim()) <= 0 || int.Parse(parts[3].Trim()) <= 0)
        {
            error = $"'{RegionArg}' must have a positive width and height (got '{raw}')";
            return false;
        }

        return true;
    }

    private static bool TryValidateStride(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;

        if (!args.TryGetValue(StrideArg, out string? raw))
            return true;

        if (!int.TryParse(raw, out int stride) || stride < 1)
        {
            error = $"'{StrideArg}' must be a whole number >= 1 (got '{raw}')";
            return false;
        }

        return true;
    }

    private static bool TryValidateDirection(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;

        if (!args.TryGetValue(DirectionArg, out string? raw) || string.IsNullOrWhiteSpace(raw))
            return true;

        if (!Directions.Contains(raw))
        {
            error = $"unknown {DirectionArg} '{raw}' (expected one of: {string.Join(", ", Directions)})";
            return false;
        }

        return true;
    }

    private static bool TryValidateBounds(IReadOnlyDictionary<string, string> args, out string? error)
    {
        error = null;

        foreach (string arg in new[] { MinDeltaEArg, MaxDeltaEArg })
        {
            if (args.TryGetValue(arg, out string? raw) && (!float.TryParse(raw, out float value) || value < 0f))
            {
                error = $"'{arg}' must be a number >= 0 (got '{raw}')";
                return false;
            }
        }

        if (TryReadFloat(args, MinDeltaEArg) is float min && TryReadFloat(args, MaxDeltaEArg) is float max
            && min > max)
        {
            error = $"'{MinDeltaEArg}' ({min}) is above '{MaxDeltaEArg}' ({max}) — no measurement can satisfy both";
            return false;
        }

        return true;
    }

    // The whole point of this tier is that a green run means something. A delta assert with no
    // direction and no bounds measures two frames and accepts every possible answer, which is a step
    // that looks like a gate and is not — the exact failure the Assert step was added to catch.
    private static bool TryValidateAssertsSomething(
        IReadOnlyDictionary<string, string> args, out string? error)
    {
        bool hasDirection = args.TryGetValue(DirectionArg, out string? direction)
                            && !string.IsNullOrWhiteSpace(direction) && direction != AnyDirection;

        if (hasDirection || TryReadFloat(args, MinDeltaEArg) != null || TryReadFloat(args, MaxDeltaEArg) != null)
        {
            error = null;
            return true;
        }

        error = $"a '{DeltaKind}' assert must declare at least one of '{DirectionArg}' (other than " +
                $"'{AnyDirection}'), '{MinDeltaEArg}' or '{MaxDeltaEArg}' — otherwise it measures the " +
                "frames and accepts every possible answer";
        return false;
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

    // Delta readers, shared with the executing half for the same reason as the vision ones: the
    // values a step acts on must be the ones TryValidate approved, not a second parse of the same
    // strings that could disagree about a default.
    public static string ReadRegion(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(RegionArg, out string? raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : FullRegion;

    public static int ReadStride(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(StrideArg, out string? raw) && int.TryParse(raw, out int stride)
            ? stride
            : DefaultStride;

    public static string ReadDirection(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(DirectionArg, out string? raw) && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : AnyDirection;

    public static float? TryReadFloat(IReadOnlyDictionary<string, string> args, string key) =>
        args.TryGetValue(key, out string? raw) && float.TryParse(raw, out float value)
            ? value
            : null;

    public static int ReadLogLines(IReadOnlyDictionary<string, string> args) =>
        args.TryGetValue(LogLinesArg, out string? raw) && int.TryParse(raw, out int lines)
            ? lines
            : DefaultLogLines;
}
