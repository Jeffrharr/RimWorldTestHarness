using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Desugars a TickLapse step into primitives the executor already knows — one
// AdvanceTicks -> (Wait) -> Screenshot triple per frame — so "film a tick-driven effect" needs no
// new game-touching code at all. Like TimelapseExpander this is pure string/number work with no
// Unity/Verse dependency, which is what keeps the whole feature unit-testable offline.
//
// WHY THIS EXISTS NEXT TO Timelapse. A Timelapse sweeps the CLOCK: every frame is an hour (or a
// fraction of one) later, which is exactly right for anything whose look is a function of the time
// of day — sun angle, sky colour, shadow direction. It is the wrong instrument for an effect that
// animates on the tick counter instead, because the two questions have different natural units and
// only one of them fits in `stepHours`:
//
//   - The interval is tiny. An aurora curtain drifts a fraction of a cell per tick, so a watchable
//     frame is ~10 ticks apart — 0.004 hours. Authoring a sweep in those numbers is a transcription
//     exercise with a rounding trap in it, and reading one back tells you nothing about what the
//     frames will look like.
//   - The span must stay INSIDE a moment, not cross it. A tick sweep is filming one continuous
//     instant of the game — a few game minutes at most — precisely so the sun does not move, the
//     weather does not change, and nothing on screen varies except the effect under test. An
//     hour-swept sequence is the opposite: it is *about* the hours changing.
//
// So the two are not the same sweep at different resolutions. Timelapse answers "what does this
// look like across a day"; TickLapse answers "what does this look like while it moves". Both emit
// the same numbered-PNG sequence and are stitched by the same runner step, because everything
// downstream of the capture genuinely is the same problem.
//
// WHY EVERY FRAME IS A JUMP, not simulation. See ClockSteps.AdvanceTicksStep: FastForward's ticks
// keep elapsing through the settle frames and the screenshot flush, so the gap between two captures
// would be the requested ticks plus an unpredictable tail. Frames spaced unevenly in game time play
// back as judder that looks like the effect stuttering. AdvanceTicks is exact.
public static class TickLapseExpander
{
    // Defaults chosen so `{"type": "TickLapse", "args": {}}` is already a watchable clip of a
    // slow-moving atmospheric effect: 120 frames 10 ticks apart is 1200 ticks — 20 game minutes,
    // well inside one lighting moment — played back at 20fps as a 6-second loop.
    public const int DefaultTicks = 10;
    public const int DefaultSteps = 120;
    public const string DefaultFileNamePrefix = "ticklapse";
    public const int DefaultFps = 20;

    // Same reasoning as Timelapse's: a jump doesn't necessarily settle everything the frame draws
    // within the same frame, and a capture taken too eagerly records the previous state.
    public const int DefaultSettleFrames = 2;

    // Same 512-frame cap as Timelapse, for the same reason: these are full-resolution PNGs, and a
    // fat-fingered `steps` should fail at load rather than fill the disk mid-run.
    public const int MaxFrames = 512;

    private static readonly string[] KnownArgs =
    {
        StepArgs.TickLapseTicks,
        StepArgs.TickLapseSteps,
        StepArgs.TickLapseFileNamePrefix,
        StepArgs.TickLapseSettleFrames,
        StepArgs.TickLapseFps,
    };

    // Returns false with a specific reason rather than throwing, so one bad scenario yields a
    // readable report entry instead of an exception during mod startup.
    public static bool TryExpand(
        IReadOnlyDictionary<string, string> args,
        out List<ScenarioStep> frames,
        out string? error)
    {
        frames = new List<ScenarioStep>();

        if (!ArgReader.ValidateKnownArgs(args, KnownArgs, out error))
            return false;
        if (!ReadParameters(args, out TickLapseParameters p, out error))
            return false;

        for (int i = 0; i < p.Steps; i++)
            AddFrame(frames, p, i);

        error = null;
        return true;
    }

    private struct TickLapseParameters
    {
        public int Ticks;
        public int Steps;
        public string FileNamePrefix;
        public int SettleFrames;
    }

    private static bool ReadParameters(
        IReadOnlyDictionary<string, string> args,
        out TickLapseParameters p,
        out string? error)
    {
        p = default;

        if (!ArgReader.TryReadInt(args, StepArgs.TickLapseTicks, DefaultTicks, out p.Ticks, out error))
            return false;
        if (!ArgReader.TryReadInt(args, StepArgs.TickLapseSteps, DefaultSteps, out p.Steps, out error))
            return false;
        if (!ArgReader.TryReadInt(args, StepArgs.TickLapseSettleFrames, DefaultSettleFrames, out p.SettleFrames, out error))
            return false;
        // Read and validated here but consumed by Runner/run_test.sh when it stitches the frames, not
        // by any step — validating it at load time means a bad value fails before the run, not after.
        if (!ArgReader.TryReadInt(args, StepArgs.TickLapseFps, DefaultFps, out int fps, out error))
            return false;

        p.FileNamePrefix = ArgReader.ReadString(args, StepArgs.TickLapseFileNamePrefix, DefaultFileNamePrefix);

        return ValidateParameters(p, fps, out error);
    }

    private static bool ValidateParameters(TickLapseParameters p, int fps, out string? error)
    {
        if (string.IsNullOrWhiteSpace(p.FileNamePrefix))
        {
            error = $"'{StepArgs.TickLapseFileNamePrefix}' must not be empty";
            return false;
        }

        // Zero ticks would capture the same instant `steps` times: a still, reported as a clip.
        if (p.Ticks <= 0)
        {
            error = $"'{StepArgs.TickLapseTicks}' must be greater than 0 (got {p.Ticks})";
            return false;
        }

        if (p.Steps <= 0)
        {
            error = $"'{StepArgs.TickLapseSteps}' must be greater than 0 (got {p.Steps})";
            return false;
        }

        if (p.Steps > MaxFrames)
        {
            error = $"{p.Steps} frames exceeds the {MaxFrames}-frame cap — lower " +
                    $"'{StepArgs.TickLapseSteps}' (raise '{StepArgs.TickLapseTicks}' to keep the same span)";
            return false;
        }

        if (p.SettleFrames < 0)
        {
            error = $"'{StepArgs.TickLapseSettleFrames}' must not be negative (got {p.SettleFrames})";
            return false;
        }

        if (fps < 1 || fps > 60)
        {
            error = $"'{StepArgs.TickLapseFps}' must be between 1 and 60 (got {fps})";
            return false;
        }

        error = null;
        return true;
    }

    // Every frame has the same shape, first one included — there is no absolute-versus-relative
    // distinction to special-case the way Timelapse's frame 0 has one. Where the clock starts is the
    // scenario's business (a SetTime before this step), and the state at that exact instant is what a
    // plain Screenshot step captures; this step is only responsible for what moves after it.
    private static void AddFrame(List<ScenarioStep> into, TickLapseParameters p, int index)
    {
        into.Add(Step(StepArgs.AdvanceTicksType, StepArgs.AdvanceTicksTicks, ArgReader.Format(p.Ticks)));

        if (p.SettleFrames > 0)
            into.Add(Step(StepArgs.WaitType, StepArgs.WaitFrames, ArgReader.Format(p.SettleFrames)));

        // Shares TimelapseExpander's zero-padded fixed-width naming so ffmpeg's %04d pattern picks
        // the sequence up, and so the runner has one stitching path rather than two.
        into.Add(Step(
            StepArgs.ScreenshotType,
            StepArgs.ScreenshotFileName,
            TimelapseExpander.FrameFileName(p.FileNamePrefix, index)));
    }

    private static ScenarioStep Step(string type, string argKey, string argValue) =>
        new ScenarioStep
        {
            Type = type,
            Args = new Dictionary<string, string> { { argKey, argValue } },
        };
}
