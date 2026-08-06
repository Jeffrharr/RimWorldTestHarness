using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// The bound a ProfileAssert step checks, parsed out of its args. Pure, so the "exactly one bound form"
// rule and every message it produces are covered offline rather than only by whatever a live run
// happened to exercise.
//
// Three forms rather than one because performance assertions are overwhelmingly one-sided — "this must
// not exceed 1 ms", "this must fire at least once per frame" — and forcing them through the Probe
// step's expectedValue±tolerance shape means writing `expectedValue: 0.5, tolerance: 0.5` and hoping
// the next reader works out that it meant "at most 1". A gate that awkward to write is a gate that
// does not get written.
public struct ProfileBound
{
    public string Comparison;   // ProbeComparison.*
    public double Expected;     // the bound itself (or the midpoint, for Within)
    public double Tolerance;    // meaningful only for Within

    public bool IsSatisfiedBy(double actual) =>
        ReportComparer.Satisfies(actual, Expected, Tolerance, Comparison);
}

public static class ProfileAssertArgs
{
    public static readonly string[] KnownArgs =
    {
        StepArgs.ProfileAssertTable,
        StepArgs.ProfileAssertLabel,
        StepArgs.ProfileAssertMetric,
        StepArgs.ProfileAssertExpectedValue,
        StepArgs.ProfileAssertTolerance,
        StepArgs.ProfileAssertMax,
        StepArgs.ProfileAssertMin,
    };

    public struct Parsed
    {
        public string Table;
        public string Label;
        public string Metric;
        public ProfileBound Bound;
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string> args, out Parsed parsed, out string? error)
    {
        parsed = default;

        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;

        parsed.Table = ArgReader.ReadString(args, StepArgs.ProfileAssertTable, "");
        // Defaulting to the whole-table pseudo-row rather than requiring a label, because "this mod
        // costs under X per frame in total" is the assertion that survives a patch being renamed or
        // split in two, and it should therefore be the easy one to write.
        parsed.Label = ArgReader.ReadString(args, StepArgs.ProfileAssertLabel, ProfileMetrics.TotalsLabel);
        parsed.Metric = ArgReader.ReadString(args, StepArgs.ProfileAssertMetric, "");

        if (string.IsNullOrWhiteSpace(parsed.Table))
        {
            error = $"'{StepArgs.ProfileAssertTable}' is required — name the Profile/ProfileStop step's " +
                    $"'{StepArgs.ProfileName}'";
            return false;
        }

        if (!ProfileMetrics.IsKnown(parsed.Metric))
        {
            error = $"'{StepArgs.ProfileAssertMetric}' must be one of: " +
                    string.Join(", ", ProfileMetrics.Known) +
                    (string.IsNullOrEmpty(parsed.Metric) ? "" : $" (got '{parsed.Metric}')");
            return false;
        }

        return TryReadBound(args, out parsed.Bound, out error);
    }

    private static bool TryReadBound(
        IReadOnlyDictionary<string, string> args, out ProfileBound bound, out string? error)
    {
        bound = default;

        bool hasExpected = args.ContainsKey(StepArgs.ProfileAssertExpectedValue);
        bool hasMax = args.ContainsKey(StepArgs.ProfileAssertMax);
        bool hasMin = args.ContainsKey(StepArgs.ProfileAssertMin);

        int forms = (hasExpected ? 1 : 0) + (hasMax ? 1 : 0) + (hasMin ? 1 : 0);
        if (forms == 0)
        {
            error = $"a ProfileAssert needs a bound: '{StepArgs.ProfileAssertMax}', " +
                    $"'{StepArgs.ProfileAssertMin}', or " +
                    $"'{StepArgs.ProfileAssertExpectedValue}' + '{StepArgs.ProfileAssertTolerance}'";
            return false;
        }

        // Refused rather than resolved by precedence. Two bounds in one step is someone expecting a
        // RANGE check, and quietly honouring only the first would pass a run that never checked the
        // other end. Two ProfileAssert steps express a range unambiguously.
        if (forms > 1)
        {
            error = $"a ProfileAssert takes exactly one of '{StepArgs.ProfileAssertMax}', " +
                    $"'{StepArgs.ProfileAssertMin}' or '{StepArgs.ProfileAssertExpectedValue}' — " +
                    "use two steps for a range";
            return false;
        }

        if (hasMax)
            return TryReadOneSided(args, StepArgs.ProfileAssertMax, ProbeComparison.AtMost, out bound, out error);

        if (hasMin)
            return TryReadOneSided(args, StepArgs.ProfileAssertMin, ProbeComparison.AtLeast, out bound, out error);

        return TryReadWithin(args, out bound, out error);
    }

    private static bool TryReadOneSided(
        IReadOnlyDictionary<string, string> args, string key, string comparison,
        out ProfileBound bound, out string? error)
    {
        bound = default;

        if (!ArgReader.TryReadDouble(args, key, 0, out double value, out error))
            return false;

        // A tolerance alongside a one-sided bound is a scenario author expecting it to widen the
        // bound. It does not, and saying so beats silently ignoring the key they wrote.
        if (args.ContainsKey(StepArgs.ProfileAssertTolerance))
        {
            error = $"'{StepArgs.ProfileAssertTolerance}' only applies to " +
                    $"'{StepArgs.ProfileAssertExpectedValue}' — fold the slack into '{key}' instead";
            return false;
        }

        bound = new ProfileBound { Comparison = comparison, Expected = value, Tolerance = 0 };
        error = null;
        return true;
    }

    private static bool TryReadWithin(
        IReadOnlyDictionary<string, string> args, out ProfileBound bound, out string? error)
    {
        bound = default;

        if (!ArgReader.TryReadDouble(args, StepArgs.ProfileAssertExpectedValue, 0, out double expected, out error))
            return false;

        // A tolerance-less expectedValue would demand an exact float match on a timing number, which
        // never passes. Required rather than defaulted, so the failure is a load error naming the
        // missing key instead of a run that always goes red.
        if (!args.ContainsKey(StepArgs.ProfileAssertTolerance))
        {
            error = $"'{StepArgs.ProfileAssertExpectedValue}' needs a " +
                    $"'{StepArgs.ProfileAssertTolerance}' — an exact match on a measured number " +
                    $"never passes (use '{StepArgs.ProfileAssertMax}' for an upper bound)";
            return false;
        }

        if (!ArgReader.TryReadDouble(args, StepArgs.ProfileAssertTolerance, 0, out double tolerance, out error))
            return false;

        bound = new ProfileBound
        {
            Comparison = ProbeComparison.Within,
            Expected = expected,
            Tolerance = tolerance,
        };
        error = null;
        return true;
    }

    // The report's name for a checked profile number: "profile:<table>/<label>.<metric>". One string
    // rather than three columns because ProbeCheckResult has one name field, and because that is the
    // form a diff between two runs' reports lines up on.
    public static string CheckName(string table, string label, string metric) =>
        $"profile:{table}/{label}.{metric}";
}
