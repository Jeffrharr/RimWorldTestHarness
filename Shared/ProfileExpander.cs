using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Desugars a Profile step into the three primitives that actually run — ProfileStart, ProfileMeasure,
// ProfileStop — so the common case ("profile this mod for 600 frames") is one line of scenario JSON
// while the uncommon case (profile whatever a hand-written span of steps does) is still reachable by
// writing the primitives out.
//
// WHY THREE PRIMITIVES AND NOT ONE. The window has to be bounded by frames that the driver actually
// lets elapse, and the driver only idles between steps (StepOutcome.WaitFrames). A single step that
// "profiles for 600 frames" would have to block inside one Root_Play.Update postfix, during which no
// frames render and nothing is profiled. So a start, a wait, and a harvest is not decomposition for
// its own sake — it is the only shape the driver's state machine can express.
//
// The split between Start and Measure exists for a different reason again: the analyzer transplants
// timing calls into the body of every profiled method, so the first call of each pays JIT for the
// rewritten IL. Start activates and warms; Measure zeroes the counters and opens the real window.
// Folding them together would attribute a one-off JIT cost to whichever patch ran first.
//
// Pure string/number work with no game types, same as TimelapseExpander and TickLapseExpander.
public static class ProfileExpander
{
    // 600 frames is a few seconds of wall clock on a machine running the harness, which is long enough
    // for per-frame means to settle and short enough that a scenario with several Profile steps is
    // still a minute-scale run. It is also comfortably under the analyzer's 2000-frame ring buffer.
    public const int DefaultFrames = 600;

    // 30 frames of warmup: enough for every transplanted method that runs at all to have run once, and
    // cheap enough that nobody is tempted to turn it off.
    public const int DefaultWarmupFrames = 30;

    // "harmony" = Analyzer's "Harmony patches" entry: every Harmony patch in the load that is not the
    // analyzer's own. That is the entry the interesting table comes from, because it attributes cost to
    // MOD patches by name rather than to vanilla methods.
    public const string DefaultEntry = "harmony";

    public const string DefaultTimeSpeed = "normal";

    // The speeds the step accepts, matching Verse.TimeSpeed's names. Validated here, in Shared, so a
    // typo fails at load rather than at execution — the Mod side maps the string to the enum and would
    // otherwise be the only thing that knew "nromal" was wrong.
    public static readonly string[] KnownTimeSpeeds =
    {
        "paused", "normal", "fast", "superfast", "ultrafast",
    };

    public static readonly string[] KnownEntries = { DefaultEntry };

    private static readonly string[] KnownArgs =
    {
        StepArgs.ProfileName,
        StepArgs.ProfilePrefix,
        StepArgs.ProfileFrames,
        StepArgs.ProfileWarmupFrames,
        StepArgs.ProfileEntry,
        StepArgs.ProfileTimeSpeed,
    };

    public struct ProfileParameters
    {
        public string Name;
        public string Prefix;
        public int Frames;
        public int WarmupFrames;
        public string Entry;
        public string TimeSpeed;
    }

    // Returns false with a specific reason rather than throwing, so one bad scenario yields a readable
    // report entry instead of an exception during mod startup.
    public static bool TryExpand(
        IReadOnlyDictionary<string, string> args,
        out List<ScenarioStep> steps,
        out string? error)
    {
        steps = new List<ScenarioStep>();

        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;
        if (!TryReadParameters(args, out ProfileParameters p, out error))
            return false;

        steps.Add(new ScenarioStep
        {
            Type = StepArgs.ProfileStartType,
            Args = new Dictionary<string, string>
            {
                { StepArgs.ProfileEntry, p.Entry },
                { StepArgs.ProfileTimeSpeed, p.TimeSpeed },
                { StepArgs.ProfileWarmupFrames, ArgReader.Format(p.WarmupFrames) },
            },
        });

        steps.Add(new ScenarioStep
        {
            Type = StepArgs.ProfileMeasureType,
            Args = new Dictionary<string, string> { { StepArgs.ProfileFrames, ArgReader.Format(p.Frames) } },
        });

        steps.Add(new ScenarioStep
        {
            Type = StepArgs.ProfileStopType,
            Args = new Dictionary<string, string>
            {
                { StepArgs.ProfileName, p.Name },
                { StepArgs.ProfilePrefix, p.Prefix },
            },
        });

        error = null;
        return true;
    }

    public static bool TryReadParameters(
        IReadOnlyDictionary<string, string> args, out ProfileParameters p, out string? error)
    {
        p = default;

        if (!ArgReader.TryReadInt(args, StepArgs.ProfileFrames, DefaultFrames, out p.Frames, out error))
            return false;
        if (!ArgReader.TryReadInt(args, StepArgs.ProfileWarmupFrames, DefaultWarmupFrames, out p.WarmupFrames, out error))
            return false;

        p.Name = ArgReader.ReadString(args, StepArgs.ProfileName, "");
        p.Prefix = ArgReader.ReadString(args, StepArgs.ProfilePrefix, "");
        p.Entry = ArgReader.ReadString(args, StepArgs.ProfileEntry, DefaultEntry);
        p.TimeSpeed = ArgReader.ReadString(args, StepArgs.ProfileTimeSpeed, DefaultTimeSpeed);

        return Validate(p, out error);
    }

    private static bool Validate(ProfileParameters p, out string? error)
    {
        if (!ValidateName(p.Name, out error))
            return false;
        if (!ValidatePrefix(p.Prefix, out error))
            return false;
        if (!ValidateFrames(p.Frames, out error))
            return false;
        if (!ValidateWarmup(p.WarmupFrames, out error))
            return false;
        if (!ValidateEntry(p.Entry, out error))
            return false;

        return ValidateTimeSpeed(p.TimeSpeed, out error);
    }

    public static bool ValidateName(string name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"'{StepArgs.ProfileName}' is required — it names the table in the report and is " +
                    "what a ProfileAssert step refers to";
            return false;
        }

        error = null;
        return true;
    }

    // Empty is refused rather than defaulted to "everything", because the default nobody chose would
    // put several thousand vanilla rows into every report and bury the three that matter.
    public static bool ValidatePrefix(string prefix, out string? error)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            error = $"'{StepArgs.ProfilePrefix}' is required — give the label prefix to keep, normally " +
                    "the mod's root namespace (the analyzer labels rows \"Namespace.Type:Method(params)\")";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateFrames(int frames, out string? error)
    {
        if (frames <= 0)
        {
            error = $"'{StepArgs.ProfileFrames}' must be greater than 0 (got {frames})";
            return false;
        }

        // Refused rather than clamped: past the analyzer's ring buffer the window silently becomes
        // "the last 1999 frames", so a scenario asking for 5000 would report means over a third of the
        // span it thinks it measured.
        if (frames > ProfileMath.MaxFrames)
        {
            error = $"'{StepArgs.ProfileFrames}' must be at most {ProfileMath.MaxFrames} " +
                    $"(got {frames}) — Dubs Performance Analyzer keeps only that many per-frame records";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateWarmup(int warmupFrames, out string? error)
    {
        if (warmupFrames < 0)
        {
            error = $"'{StepArgs.ProfileWarmupFrames}' must not be negative (got {warmupFrames})";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateEntry(string entry, out string? error)
    {
        if (Array.IndexOf(KnownEntries, entry) < 0)
        {
            error = $"unknown '{StepArgs.ProfileEntry}' value '{entry}' " +
                    $"(expected one of: {string.Join(", ", KnownEntries)})";
            return false;
        }

        error = null;
        return true;
    }

    public static bool ValidateTimeSpeed(string timeSpeed, out string? error)
    {
        if (Array.IndexOf(KnownTimeSpeeds, timeSpeed) < 0)
        {
            error = $"unknown '{StepArgs.ProfileTimeSpeed}' value '{timeSpeed}' " +
                    $"(expected one of: {string.Join(", ", KnownTimeSpeeds)})";
            return false;
        }

        error = null;
        return true;
    }
}
