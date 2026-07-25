using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimWorldTestHarness.Shared;

// Shared reading/formatting of a step's string/string Args bag. Extracted so TimelapseExpander and
// SceneLayout can't drift on two things that must stay identical across every step type:
//
//   * Invariant culture. A scenario file is data that has to read the same on any machine, and a
//     locale where "0.5" parses as 5 (or formats as "0,5") would silently shift a sweep or a
//     placement rather than failing. StepExecutor's own bare float.Parse/int.Parse calls predate
//     this and remain culture-sensitive; every arg parsed through here is not.
//   * Absent-means-default. A missing key falls back rather than erroring, so adding an optional
//     arg to a step never invalidates existing scenario files.
//   * Error message shape. Validation failures are reported to a human reading a run's report, so
//     they name the offending key AND echo the value that was rejected.
//
// Everything returns false with a reason instead of throwing: these run during mod startup, where an
// exception would leave Runner/run_test.sh waiting on a report file that never appears.
internal static class ArgReader
{
    // Rejects unknown keys rather than ignoring them. Args is an ordinary case-sensitive dictionary,
    // so a "stephours" or "spaceing" typo would otherwise fall back to the default and produce a
    // plausible-looking but wrong result — the worst kind of failure for a verification tool.
    public static bool ValidateKnownArgs(
        IReadOnlyDictionary<string, string> args, string[] knownArgs, out string? error)
    {
        foreach (KeyValuePair<string, string> arg in args)
        {
            if (Array.IndexOf(knownArgs, arg.Key) < 0)
            {
                error = $"unknown arg '{arg.Key}' (expected one of: {string.Join(", ", knownArgs)})";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryReadDouble(
        IReadOnlyDictionary<string, string> args, string key, double fallback,
        out double value, out string? error)
    {
        if (!args.TryGetValue(key, out string? raw))
        {
            value = fallback;
            error = null;
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{key}' is not a number (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryReadInt(
        IReadOnlyDictionary<string, string> args, string key, int fallback,
        out int value, out string? error)
    {
        if (!args.TryGetValue(key, out string? raw))
        {
            value = fallback;
            error = null;
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"'{key}' is not a whole number (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    // Accepts only "true"/"false" (case-insensitively). Deliberately not "1"/"yes"/"on": a scenario
    // file is data read by humans and re-parsed by the executor, and quietly accepting a fourth
    // spelling is how a typo like "ture" ends up silently meaning false.
    public static bool TryReadBool(
        IReadOnlyDictionary<string, string> args, string key, bool fallback,
        out bool value, out string? error)
    {
        if (!args.TryGetValue(key, out string? raw))
        {
            value = fallback;
            error = null;
            return true;
        }

        if (!bool.TryParse(raw, out value))
        {
            error = $"'{key}' must be true or false (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }

    public static string ReadString(
        IReadOnlyDictionary<string, string> args, string key, string fallback) =>
        args.TryGetValue(key, out string? raw) ? raw : fallback;

    // "0.####" rather than a round-trip format: these strings are re-parsed by the executor and also
    // read by humans in report entries, so trailing-zero noise is worse than the lost precision.
    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    public static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Parses an "x,z" integer pair — the compact encoding scene-setup args use for anchors, offsets
    // and explicit cells. Kept here rather than in SceneLayout because the string/string Args bag
    // makes "two numbers in one value" a recurring need for any future step with 2-D coordinates.
    public static bool TryReadIntPair(string raw, string key, out int first, out int second, out string? error)
    {
        first = 0;
        second = 0;

        string[] parts = raw.Split(',');
        if (parts.Length != 2)
        {
            error = $"'{key}' must be two comma-separated whole numbers like \"12,-4\" (got '{raw}')";
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out first) ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out second))
        {
            error = $"'{key}' must be two comma-separated whole numbers like \"12,-4\" (got '{raw}')";
            return false;
        }

        error = null;
        return true;
    }
}
