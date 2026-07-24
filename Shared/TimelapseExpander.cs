using System;
using System.Collections.Generic;
using System.Globalization;

namespace RimWorldTestHarness.Shared;

// Desugars a Timelapse step into the primitives the executor already knows — one
// SetTime -> (Wait) -> Screenshot triple per frame of the sweep — so "record a video" needs no new
// game-touching code at all. Everything here is pure string/number work with no Unity/Verse
// dependency, which is the point: the whole feature is unit-testable offline, and StepExecutor and
// ScenarioDriver stay untouched.
//
// Why sweep the CLOCK rather than record wall-clock time: the harness already jumps the game clock
// deterministically (StepExecutor.SetTime -> DebugSetTicksGame). A clock-swept frame sequence is
// reproducible and frame-aligned, so the same scenario run against two builds of a mod under test
// produces two videos you can compare frame-for-frame. A real-time screen recording drifts with
// framerate and can't do that. (It's also the only route that works at all here — this is a
// Wayland session, where grabbing the game window from outside needs either elevated privileges or
// an interactive portal prompt, both hostile to an unattended run.)
//
// Range is HALF-OPEN, [fromHour, toHour). A full-day 0->24 sweep therefore yields 24 distinct
// hourly frames rather than 25 with midnight duplicated at both ends, so the stitched video loops
// seamlessly. To include the endpoint, extend toHour by one step.
public static class TimelapseExpander
{
    // Defaults chosen so `{"type": "Timelapse", "args": {}}` means "one full day, hourly" — the
    // common case for reviewing a lighting mod.
    public const double DefaultFromHour = 0;
    public const double DefaultToHour = 24;
    public const double DefaultStepHours = 1;
    public const string DefaultFileNamePrefix = "timelapse";
    public const int DefaultFps = 12;

    // A clock jump doesn't necessarily settle the glow grid and shadow direction in the same frame,
    // and a frame captured mid-update would show stale lighting — the failure mode that a single
    // screenshot never exposed but a 24-frame sweep would. Two frames is a cheap hedge; scenarios
    // that still see smearing should raise it. This default is a reasoned guess, not a measured
    // value: it needs confirming against a real run.
    public const int DefaultSettleFrames = 2;

    // Guards against a fat-fingered stepHours (0.001 would be 24,000 frames and tens of GB of PNGs)
    // turning a test run into a disk-filling incident.
    public const int MaxFrames = 512;

    private static readonly string[] KnownArgs =
    {
        StepArgs.TimelapseFromHour,
        StepArgs.TimelapseToHour,
        StepArgs.TimelapseStepHours,
        StepArgs.TimelapseFileNamePrefix,
        StepArgs.TimelapseSettleFrames,
        StepArgs.TimelapseFps,
    };

    // Expands every Timelapse step in a scenario, passing all other steps through untouched. A
    // Timelapse whose args don't validate is left in place and its reason appended to errors — the
    // driver surfaces those in the run's report, and StepExecutor fails the leftover step rather
    // than silently skipping it. Failing loudly beats a run that quietly produces no frames.
    public static List<ScenarioStep> ExpandAll(IEnumerable<ScenarioStep> steps, List<string> errors)
    {
        List<ScenarioStep> expanded = new List<ScenarioStep>();
        foreach (ScenarioStep step in steps)
            ExpandInto(step, expanded, errors);
        return expanded;
    }

    private static void ExpandInto(ScenarioStep step, List<ScenarioStep> into, List<string> errors)
    {
        if (step.Type != StepArgs.TimelapseType)
        {
            into.Add(step);
            return;
        }

        if (!TryExpand(step.Args, out List<ScenarioStep> frames, out string? error))
        {
            errors.Add($"Timelapse step is invalid and was not expanded: {error}");
            into.Add(step);
            return;
        }

        into.AddRange(frames);
    }

    // The expansion proper. Returns false with a specific reason rather than throwing, so one bad
    // scenario yields a readable report entry instead of an exception during mod startup (which
    // would leave the runner waiting on a report file that never appears).
    public static bool TryExpand(
        IReadOnlyDictionary<string, string> args,
        out List<ScenarioStep> frames,
        out string? error)
    {
        frames = new List<ScenarioStep>();

        if (!ValidateKnownArgs(args, out error))
            return false;
        if (!ReadParameters(args, out TimelapseParameters p, out error))
            return false;
        if (!FrameCount(p, out int count, out error))
            return false;

        for (int i = 0; i < count; i++)
            AddFrame(frames, p, i);

        error = null;
        return true;
    }

    // Unknown keys are rejected rather than ignored: Args is an ordinary case-sensitive dictionary,
    // so a "stephours" typo would otherwise silently fall back to the default and produce a
    // plausible-looking but wrong video — the worst kind of failure for a verification tool.
    private static bool ValidateKnownArgs(IReadOnlyDictionary<string, string> args, out string? error)
    {
        foreach (KeyValuePair<string, string> arg in args)
        {
            if (Array.IndexOf(KnownArgs, arg.Key) < 0)
            {
                error = $"unknown arg '{arg.Key}' (expected one of: {string.Join(", ", KnownArgs)})";
                return false;
            }
        }

        error = null;
        return true;
    }

    private struct TimelapseParameters
    {
        public double FromHour;
        public double ToHour;
        public double StepHours;
        public string FileNamePrefix;
        public int SettleFrames;
    }

    private static bool ReadParameters(
        IReadOnlyDictionary<string, string> args,
        out TimelapseParameters p,
        out string? error)
    {
        p = default;

        if (!ReadDouble(args, StepArgs.TimelapseFromHour, DefaultFromHour, out p.FromHour, out error))
            return false;
        if (!ReadDouble(args, StepArgs.TimelapseToHour, DefaultToHour, out p.ToHour, out error))
            return false;
        if (!ReadDouble(args, StepArgs.TimelapseStepHours, DefaultStepHours, out p.StepHours, out error))
            return false;
        if (!ReadInt(args, StepArgs.TimelapseSettleFrames, DefaultSettleFrames, out p.SettleFrames, out error))
            return false;
        // Read only to validate it here; fps is consumed by Runner/run_test.sh when it stitches the
        // frames, not by any step. Validating it at load time means a bad value fails the scenario
        // up front instead of after the run has spent minutes producing frames.
        if (!ReadInt(args, StepArgs.TimelapseFps, DefaultFps, out int fps, out error))
            return false;

        p.FileNamePrefix = args.TryGetValue(StepArgs.TimelapseFileNamePrefix, out string? prefix)
            ? prefix
            : DefaultFileNamePrefix;

        return ValidateParameters(p, fps, out error);
    }

    private static bool ValidateParameters(TimelapseParameters p, int fps, out string? error)
    {
        if (string.IsNullOrWhiteSpace(p.FileNamePrefix))
        {
            error = $"'{StepArgs.TimelapseFileNamePrefix}' must not be empty";
            return false;
        }

        if (p.StepHours <= 0)
        {
            error = $"'{StepArgs.TimelapseStepHours}' must be greater than 0 (got {Format(p.StepHours)})";
            return false;
        }

        // SetTime's own contract is an hour within a single day; the clock jump clamps outside it,
        // which would silently pin every out-of-range frame to the same instant.
        if (p.FromHour < 0 || p.ToHour > 24)
        {
            error = $"hour range must lie within 0..24 (got {Format(p.FromHour)}..{Format(p.ToHour)})";
            return false;
        }

        if (p.FromHour >= p.ToHour)
        {
            error = $"'{StepArgs.TimelapseFromHour}' must be less than '{StepArgs.TimelapseToHour}' " +
                    $"(got {Format(p.FromHour)}..{Format(p.ToHour)}); a sweep across midnight isn't " +
                    "supported — use two Timelapse steps";
            return false;
        }

        if (p.SettleFrames < 0)
        {
            error = $"'{StepArgs.TimelapseSettleFrames}' must not be negative (got {p.SettleFrames})";
            return false;
        }

        if (fps < 1 || fps > 60)
        {
            error = $"'{StepArgs.TimelapseFps}' must be between 1 and 60 (got {fps})";
            return false;
        }

        error = null;
        return true;
    }

    private static bool FrameCount(TimelapseParameters p, out int count, out string? error)
    {
        // Rounded before the ceiling because binary floating point makes an exact division land just
        // either side of a whole number (24/1 can compute as 23.999999...), and an unrounded Ceiling
        // would turn that into an extra duplicate frame.
        double exact = (p.ToHour - p.FromHour) / p.StepHours;
        count = (int)Math.Ceiling(Math.Round(exact, 6));

        if (count > MaxFrames)
        {
            error = $"{count} frames exceeds the {MaxFrames}-frame cap — raise " +
                    $"'{StepArgs.TimelapseStepHours}' (currently {Format(p.StepHours)})";
            return false;
        }

        error = null;
        return true;
    }

    private static void AddFrame(List<ScenarioStep> into, TimelapseParameters p, int index)
    {
        // Computed from the index rather than accumulated, so rounding error can't drift across a
        // long sweep.
        double hour = p.FromHour + index * p.StepHours;

        into.Add(Step(StepArgs.SetTimeType, StepArgs.SetTimeHour, Format(hour)));

        if (p.SettleFrames > 0)
            into.Add(Step(StepArgs.WaitType, StepArgs.WaitFrames, p.SettleFrames.ToString(CultureInfo.InvariantCulture)));

        // Zero-padded and fixed-width so the frames sort correctly and ffmpeg's %04d pattern picks
        // them up as a sequence.
        into.Add(Step(StepArgs.ScreenshotType, StepArgs.ScreenshotFileName, FrameFileName(p.FileNamePrefix, index)));
    }

    public static string FrameFileName(string prefix, int index) =>
        $"{prefix}_{index.ToString("D4", CultureInfo.InvariantCulture)}.png";

    private static ScenarioStep Step(string type, string argKey, string argValue) =>
        new ScenarioStep
        {
            Type = type,
            Args = new Dictionary<string, string> { { argKey, argValue } },
        };

    // Invariant culture throughout: a scenario file is data that has to read the same on any
    // machine, and the executor parses these strings straight back out.
    private static string Format(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool ReadDouble(
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

    private static bool ReadInt(
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
}
