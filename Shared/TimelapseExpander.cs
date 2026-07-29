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
//
// The range MAY WRAP past midnight: toHour below fromHour (16->4) means "16:00 through to 04:00 the
// next morning", 12 hours, with each frame's hour taken modulo 24. Half-open still holds, so 16->4
// ends at 03:xx. This is the shape every dusk-to-moonlight sweep wants, and splitting it into a
// 16->24 and a 0->4 step cannot substitute: the two would either collide on one prefix's frame
// numbering or stitch as two videos with a cut at the exact moment being filmed.
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

    // The frame count, stated directly. `toHour` says where to stop and lets the count fall out of
    // the arithmetic; `steps` says how many frames and lets the end fall out instead. Both describe
    // the same sweep, but `steps` is the one that controls what actually matters downstream — a gif
    // on a store page has a hard byte budget, and bytes are frames. It also expresses a full day
    // from ANY starting hour without an ambiguous 12..12, which is the sweep a looping video wants:
    // start at noon and the loop's seam lands where shadows are shortest instead of where they are
    // longest and most directional.
    public const string StepsArg = "steps";

    private static readonly string[] KnownArgs =
    {
        StepArgs.TimelapseFromHour,
        StepArgs.TimelapseToHour,
        StepsArg,
        StepArgs.TimelapseStepHours,
        StepArgs.TimelapseFileNamePrefix,
        StepArgs.TimelapseSettleFrames,
        StepArgs.TimelapseFps,
    };

    // Walking the step list and deciding what to do with a failed expansion now lives in
    // Steps/StepExpansion.cs, which does it for any registered composite rather than for Timelapse
    // specifically. What remains here is the Timelapse-specific arithmetic, reached through
    // Steps/BuiltIn/TimelapseStep.cs.
    //
    // The expansion proper. Returns false with a specific reason rather than throwing, so one bad
    // scenario yields a readable report entry instead of an exception during mod startup (which
    // would leave the runner waiting on a report file that never appears).
    public static bool TryExpand(
        IReadOnlyDictionary<string, string> args,
        out List<ScenarioStep> frames,
        out string? error)
    {
        frames = new List<ScenarioStep>();

        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
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

    private struct TimelapseParameters
    {
        public double FromHour;
        public double ToHour;
        // 0 means "not given" — the sweep is then bounded by ToHour, as it always was.
        public int Steps;
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

        if (!ArgReader.TryReadDouble(args, StepArgs.TimelapseFromHour, DefaultFromHour, out p.FromHour, out error))
            return false;
        if (!ArgReader.TryReadDouble(args, StepArgs.TimelapseToHour, DefaultToHour, out p.ToHour, out error))
            return false;
        if (!ArgReader.TryReadInt(args, StepsArg, 0, out p.Steps, out error))
            return false;
        if (!ArgReader.TryReadDouble(args, StepArgs.TimelapseStepHours, DefaultStepHours, out p.StepHours, out error))
            return false;
        if (!ArgReader.TryReadInt(args, StepArgs.TimelapseSettleFrames, DefaultSettleFrames, out p.SettleFrames, out error))
            return false;
        // Read only to validate it here; fps is consumed by Runner/run_test.sh when it stitches the
        // frames, not by any step. Validating it at load time means a bad value fails the scenario
        // up front instead of after the run has spent minutes producing frames.
        if (!ArgReader.TryReadInt(args, StepArgs.TimelapseFps, DefaultFps, out int fps, out error))
            return false;

        p.FileNamePrefix = ArgReader.ReadString(args, StepArgs.TimelapseFileNamePrefix, DefaultFileNamePrefix);

        // Both bound the sweep, so giving both is two answers to one question. Honouring `steps`
        // and ignoring `toHour` would be the quiet option and the wrong one: the author would get a
        // video that stops somewhere other than where they wrote, and nothing would say so.
        if (p.Steps > 0 && args.ContainsKey(StepArgs.TimelapseToHour))
        {
            error = $"'{StepsArg}' and '{StepArgs.TimelapseToHour}' both bound the sweep — give one " +
                    $"('{StepsArg}' for an exact frame count, '{StepArgs.TimelapseToHour}' for an end hour)";
            return false;
        }

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

        // toHour BELOW fromHour is not an error — it means the sweep wraps past midnight, and each
        // frame's hour is taken modulo 24 (see SpanHours/AddFrame). This used to be rejected with
        // "use two Timelapse steps", which was the wrong answer for the case that motivated the
        // feature: filming dusk through to a high moon. Two steps cannot express it, because each
        // writes its own prefix_0000.png sequence and the second either overwrites the first or
        // stitches into a separate video with a cut at midnight — precisely in the middle of the
        // moonrise the video exists to show.
        //
        // Equality is rejected, but only when `steps` is absent: 12..12 reads as both an empty
        // sweep and a full day, and `steps` says which you meant without anyone having to guess.
        if (p.Steps == 0 && p.FromHour == p.ToHour)
        {
            error = $"'{StepArgs.TimelapseFromHour}' and '{StepArgs.TimelapseToHour}' are both " +
                    $"{Format(p.FromHour)}, which reads as either an empty sweep or a whole day — " +
                    $"say which with '{StepsArg}' (e.g. fromHour 12, stepHours 0.25, {StepsArg} 96)";
            return false;
        }

        if (p.Steps < 0)
        {
            error = $"'{StepsArg}' must not be negative (got {p.Steps})";
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
        // An explicit step count needs no arithmetic and no rounding — it IS the answer.
        if (p.Steps > 0)
        {
            count = p.Steps;
            return WithinCap(count, p, out error);
        }

        double exact = SpanHours(p) / p.StepHours;
        count = (int)Math.Ceiling(Math.Round(exact, 6));

        return WithinCap(count, p, out error);
    }

    private static bool WithinCap(int count, TimelapseParameters p, out string? error)
    {
        if (count > MaxFrames)
        {
            error = $"{count} frames exceeds the {MaxFrames}-frame cap — raise " +
                    $"'{StepArgs.TimelapseStepHours}' (currently {Format(p.StepHours)}) or lower " +
                    $"'{StepsArg}'";
            return false;
        }

        error = null;
        return true;
    }

    // Hours covered by the sweep. A wrapping range (toHour below fromHour) runs to midnight and on
    // into the next day, so its span is the two pieces added, not the negative difference.
    private static double SpanHours(TimelapseParameters p) =>
        p.ToHour > p.FromHour ? p.ToHour - p.FromHour : 24 - p.FromHour + p.ToHour;
    // Equal hours fall into the second branch and yield 24 - h + h = 24, which is exactly the
    // full-day reading documented above — no special case needed.

    private static void AddFrame(List<ScenarioStep> into, TimelapseParameters p, int index)
    {
        // Frame 0 is the only ABSOLUTE jump; every later frame ADVANCES by one step.
        //
        // This used to be one SetTime per frame, with the hour computed as
        // (FromHour + index * StepHours) % 24. That modulo is what broke sweeps crossing midnight:
        // SetTime pins the current day, so asking for 00:00 after 23:45 rewound the clock nearly a
        // full day instead of stepping forward 15 minutes. Hour-of-day effects could not tell —
        // the sun sits at the same angle at 00:00 either way — but the moon is driven by absolute
        // time, so it jumped a day backwards and its shadows swung to a new direction on that one
        // frame, mid-video. Relative steps cannot express that error.
        //
        // It also removes the need to know anything about wrapping here: the clock rolls into the
        // next day by itself, exactly as it would if the game were running.
        if (index == 0)
            into.Add(Step(StepArgs.SetTimeType, StepArgs.SetTimeHour, Format(p.FromHour)));
        else
            into.Add(Step(StepArgs.AdvanceTimeType, StepArgs.AdvanceTimeHours, Format(p.StepHours)));

        if (p.SettleFrames > 0)
            into.Add(Step(StepArgs.WaitType, StepArgs.WaitFrames, ArgReader.Format(p.SettleFrames)));

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

    // Invariant culture throughout (see ArgReader): a scenario file is data that has to read the
    // same on any machine, and the executor parses these strings straight back out.
    private static string Format(double value) => ArgReader.Format(value);
}
