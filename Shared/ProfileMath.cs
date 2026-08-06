using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RimWorldTestHarness.Shared;

// The pure half of profiling: raw analyzer samples in, a report-ready ProfileTable out. No Verse, no
// UnityEngine, no reflection — so every division in here is covered by offline unit tests rather than
// only by whatever a live run happened to exercise.
//
// The arithmetic is trivial and the ways to get it quietly wrong are not, which is the whole argument
// for pulling it out of the adapter: a mean divided by the frames we ASKED for rather than the frames
// that were actually recorded, a percentage of a denominator nobody wrote down, a per-call figure over
// a zero call count. Each of those produces a plausible number, and a plausible wrong number in a
// verification tool is worse than a crash.
public static class ProfileMath
{
    // A 60 fps frame in milliseconds. The budget a human means when they ask what share of the frame
    // something costs — see ProfileTable.FrameMs for why the analyzer's own percentage is not that.
    public const double SixtyFpsFrameMs = 1000.0 / 60.0;

    // The analyzer keeps 2000 per-frame records in a ring buffer and its own statistics pass reads at
    // most 1999 of them (Analyzer.Profiling.Profiler.RECORDS_HELD / ProfileCalculations). A longer
    // window silently measures only the last ~2000 frames, so the step refuses one instead.
    public const int MaxFrames = 1999;

    // Caveats recorded INTO every table. They are here rather than only in README.md because the place
    // someone misreads these numbers is while staring at a report, and a note in the report is the only
    // documentation that reliably arrives at that moment.
    public static readonly string CallCountNote =
        "Calls counts every entry into the method, including invocations that hit a guard clause and " +
        "return immediately. AvgUsPerCall is therefore a mean over expensive and trivial calls " +
        "together and understates the cost of the calls that do work; MaxCallsPerFrame is the only " +
        "shape information available behind that mean.";

    public static readonly string PercentNote =
        "PercentOfFrame is a share of FrameMs — the frame time this machine actually achieved during " +
        "the window, which under a test harness is usually far shorter than 16.67 ms. Compare " +
        "PercentOfSixtyFpsBudget before concluding anything is expensive.";

    public static readonly string ProfilerOverheadNote =
        "Measured with Dubs Performance Analyzer transplanting timing calls into every profiled " +
        "method. Absolute figures carry that overhead; the numbers worth trusting are ratios between " +
        "rows and changes in the same row between builds.";

    // Builds the report table. `measuredFrames` is the analyzer's own count of recorded frames, NOT
    // the number the scenario asked for: everything below divides by it.
    public static ProfileTable Build(
        string name,
        string entry,
        string prefix,
        int requestedFrames,
        int measuredFrames,
        double frameMs,
        IEnumerable<ProfileSample> samples)
    {
        List<ProfileSample> all = samples?.ToList() ?? new List<ProfileSample>();
        List<ProfileRow> rows = all
            .Where(s => MatchesPrefix(s.Label, prefix))
            .Select(s => Row(s, measuredFrames, frameMs))
            .OrderByDescending(r => r.AvgMsPerFrame)
            .ThenBy(r => r.Label, StringComparer.Ordinal)
            .ToList();

        ProfileTable table = new ProfileTable
        {
            Name = name,
            Entry = entry,
            Prefix = prefix,
            RequestedFrames = requestedFrames,
            MeasuredFrames = measuredFrames,
            FrameMs = Round(frameMs, 4),
            FramesPerSecond = Round(Divide(1000.0, frameMs), 2),
            RowsBeforeFilter = all.Count,
            Rows = rows,
        };

        table.TotalAvgMsPerFrame = Round(rows.Sum(r => r.AvgMsPerFrame), 4);
        table.TotalPercentOfFrame = Round(Percent(table.TotalAvgMsPerFrame, frameMs), 3);
        table.TotalPercentOfSixtyFpsBudget = Round(Percent(table.TotalAvgMsPerFrame, SixtyFpsFrameMs), 3);
        table.Notes.Add(CallCountNote);
        table.Notes.Add(PercentNote);
        table.Notes.Add(ProfilerOverheadNote);
        return table;
    }

    // Empty prefix means "every profiled method in the load", which is thousands of rows of vanilla —
    // legal, and occasionally what you want, but never the default a scenario should get by accident.
    // The step spec requires the arg for that reason; this stays permissive so the filtering rule has
    // one definition.
    //
    // Ordinal, case-sensitive, and anchored at the start, because the analyzer's label is
    // "Namespace.Type:Method" and a mod's root namespace is exactly the prefix an author would write.
    // Case-insensitive matching would let "celestiallighting" quietly work here and fail nowhere else,
    // which is how a scenario ends up depending on a spelling it never verified.
    public static bool MatchesPrefix(string? label, string? prefix) =>
        string.IsNullOrEmpty(prefix) ||
        (label != null && label.StartsWith(prefix, StringComparison.Ordinal));

    private static ProfileRow Row(ProfileSample sample, int measuredFrames, double frameMs)
    {
        double avgMs = sample.AverageMs;
        return new ProfileRow
        {
            Label = sample.Label ?? "",
            AvgMsPerFrame = Round(avgMs, 4),
            MaxMsPerFrame = Round(sample.MaxMs, 4),
            TotalMs = Round(sample.TotalMs, 3),
            Calls = Round(sample.Calls, 0),
            CallsPerFrame = Round(Divide(sample.Calls, measuredFrames), 3),
            MaxCallsPerFrame = Round(sample.MaxCallsPerFrame, 0),
            // Microseconds: a per-call figure in milliseconds is four leading zeros for anything that
            // isn't already a problem, and the interesting range here is single-digit to low-hundreds µs.
            AvgUsPerCall = Round(Divide(sample.TotalMs * 1000.0, sample.Calls), 2),
            PercentOfFrame = Round(Percent(avgMs, frameMs), 3),
            PercentOfSixtyFpsBudget = Round(Percent(avgMs, SixtyFpsFrameMs), 3),
        };
    }

    // Zero rather than infinity/NaN for an empty divisor. A NaN would travel into the JSON report and
    // break System.Text.Json's writer; zero is also the honest answer to "how many microseconds did
    // each of nought calls take".
    private static double Divide(double numerator, double denominator) =>
        denominator <= 0 ? 0 : numerator / denominator;

    private static double Percent(double part, double whole) => Divide(part * 100.0, whole);

    // Rounded before it reaches the report so two runs of an unchanged build produce diffable JSON
    // instead of noise in the fifteenth decimal place. MidpointRounding.AwayFromZero because banker's
    // rounding on a report a human reads is a surprise nobody asked for.
    private static double Round(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);
}

// Resolving "which number in this table" for a ProfileAssert step. Split out from ProfileMath because
// it is name-lookup rather than arithmetic, and because getting a metric name wrong must be a loud
// load-time error rather than an assert that silently reads zero.
public static class ProfileMetrics
{
    // The whole-table pseudo-row: a `label` of "*" asserts on the table's totals rather than on one
    // patch. Exists because "this mod costs under X ms/frame in total" is the assertion people
    // actually want, and spelling it out per patch would break every time a patch is added or split.
    public const string TotalsLabel = "*";

    public const string AvgMsPerFrame = "avgMsPerFrame";
    public const string MaxMsPerFrame = "maxMsPerFrame";
    public const string TotalMs = "totalMs";
    public const string Calls = "calls";
    public const string CallsPerFrame = "callsPerFrame";
    public const string MaxCallsPerFrame = "maxCallsPerFrame";
    public const string AvgUsPerCall = "avgUsPerCall";
    public const string PercentOfFrame = "percentOfFrame";
    public const string PercentOfSixtyFpsBudget = "percentOfSixtyFpsBudget";

    public static readonly string[] Known =
    {
        AvgMsPerFrame, MaxMsPerFrame, TotalMs, Calls, CallsPerFrame,
        MaxCallsPerFrame, AvgUsPerCall, PercentOfFrame, PercentOfSixtyFpsBudget,
    };

    public static bool IsKnown(string metric) => Array.IndexOf(Known, metric) >= 0;

    // Reads one metric out of a table. Returns false with a reason for anything ambiguous or absent,
    // never a default: a ProfileAssert that silently read 0 for a patch whose name changed would pass
    // whatever bound it was given, which is the "green run verified nothing" failure this repo exists
    // to prevent.
    public static bool TryRead(
        ProfileTable table, string label, string metric, out double value, out string? error)
    {
        value = 0;

        if (!IsKnown(metric))
        {
            error = $"unknown profile metric '{metric}' (expected one of: {string.Join(", ", Known)})";
            return false;
        }

        if (label == TotalsLabel)
        {
            value = ReadTotal(table, metric);
            error = null;
            return true;
        }

        if (!TryFindRow(table, label, out ProfileRow? row, out error))
            return false;

        value = ReadRow(row!, metric);
        error = null;
        return true;
    }

    // Exact match first, then a unique substring match. The substring path exists because the
    // analyzer's labels are long ("CelestialLighting.Patch_GameConditionManager_Tick:Postfix") and a
    // scenario naming the whole thing breaks on any rename of a private patch class. AMBIGUITY IS AN
    // ERROR rather than first-match-wins: two patches whose names share a fragment is exactly when
    // picking silently would assert against the wrong one.
    private static bool TryFindRow(ProfileTable table, string label, out ProfileRow? row, out string? error)
    {
        row = table.Rows.FirstOrDefault(r => string.Equals(r.Label, label, StringComparison.Ordinal));
        if (row != null)
        {
            error = null;
            return true;
        }

        List<ProfileRow> partial = table.Rows
            .Where(r => r.Label.IndexOf(label, StringComparison.Ordinal) >= 0)
            .ToList();

        if (partial.Count == 1)
        {
            row = partial[0];
            error = null;
            return true;
        }

        if (partial.Count > 1)
        {
            error = $"'{label}' matches {partial.Count} rows in profile table '{table.Name}' " +
                    $"({string.Join(", ", partial.Take(3).Select(r => r.Label))}…) — name one exactly";
            return false;
        }

        error = $"no profiled method matching '{label}' in profile table '{table.Name}' " +
                $"({table.Rows.Count} rows matched prefix '{table.Prefix}')";
        return false;
    }

    private static double ReadRow(ProfileRow row, string metric)
    {
        switch (metric)
        {
            case AvgMsPerFrame: return row.AvgMsPerFrame;
            case MaxMsPerFrame: return row.MaxMsPerFrame;
            case TotalMs: return row.TotalMs;
            case Calls: return row.Calls;
            case CallsPerFrame: return row.CallsPerFrame;
            case MaxCallsPerFrame: return row.MaxCallsPerFrame;
            case AvgUsPerCall: return row.AvgUsPerCall;
            case PercentOfFrame: return row.PercentOfFrame;
            default: return row.PercentOfSixtyFpsBudget;
        }
    }

    // Totals are per-metric rather than a blanket sum, because summing a MAX would be nonsense: the
    // worst frame for the subsystem is the worst frame any one of its patches had, not the sum of
    // their individual worst frames (which were probably different frames).
    private static double ReadTotal(ProfileTable table, string metric)
    {
        switch (metric)
        {
            case AvgMsPerFrame: return table.TotalAvgMsPerFrame;
            case MaxMsPerFrame: return table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.MaxMsPerFrame);
            case TotalMs: return table.Rows.Sum(r => r.TotalMs);
            case Calls: return table.Rows.Sum(r => r.Calls);
            case CallsPerFrame: return table.Rows.Sum(r => r.CallsPerFrame);
            case MaxCallsPerFrame: return table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.MaxCallsPerFrame);
            // The subsystem's total time over its total calls — deliberately NOT the mean of the rows'
            // means, which would weight a method called twice the same as one called ten thousand times.
            case AvgUsPerCall: return SafeDivide(table.Rows.Sum(r => r.TotalMs) * 1000.0, table.Rows.Sum(r => r.Calls));
            case PercentOfFrame: return table.TotalPercentOfFrame;
            default: return table.TotalPercentOfSixtyFpsBudget;
        }
    }

    private static double SafeDivide(double numerator, double denominator) =>
        denominator <= 0 ? 0 : numerator / denominator;

    public static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
