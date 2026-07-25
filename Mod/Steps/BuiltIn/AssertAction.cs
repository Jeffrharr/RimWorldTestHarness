using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorldTestHarness.Shared;
using RimWorldTestHarness.Shared.Steps.BuiltIn;
using Verse;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

// Builds the review packet: resolve the named screenshots to real paths, snapshot the game's recent
// warnings and errors, and hand the whole thing back for the driver to record. It does not judge
// anything — that is deliberate, and the reason this tier needs no API key (see Shared/VisionAssert).
public sealed class AssertAction : IStepAction
{
    public string Type => AssertStep.StepType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        List<string> names = AssertStep.ParseImages(args[AssertStep.ImagesArg]);
        List<string> paths = names.Select(ctx.ResolveScreenshotPath).ToList();

        // A rubric pointing at an image that was never captured would send the judge a packet it
        // cannot answer, and "the judge couldn't see it" is indistinguishable from "the effect wasn't
        // there" — a false FAIL. Named explicitly so the scenario author can fix the typo.
        //
        // Reaching this check means the name really is wrong, not that we were too early: the driver
        // holds the run after every Screenshot until the PNG is on disk (StepHelpers.
        // ScreenshotComplete), and gives up with its own error if it never lands. That guarantee is
        // new — the first live run of this step failed here because the driver used to wait a flat
        // five frames and the last screenshot before an Assert hadn't flushed yet.
        List<string> missing = paths.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            return StepOutcome.Fail(
                $"Assert: no screenshot at {string.Join(", ", missing.Select(Path.GetFileName))} — " +
                "the 'images' arg must name fileNames captured by earlier Screenshot steps in this scenario");
        }

        return new StepOutcome
        {
            VisionAssert = new VisionAssert
            {
                Id = args.TryGetValue(AssertStep.IdArg, out string? id) && !string.IsNullOrWhiteSpace(id)
                    ? id
                    : "",   // the driver fills in a step-index default, which is the only thing that knows the index
                Prompt = args[AssertStep.PromptArg],
                Expect = args.TryGetValue(AssertStep.ExpectArg, out string? expect) ? expect : "",
                ConfidenceGate = AssertStep.ReadConfidenceGate(args),
                ImagePaths = paths,
                LogExcerpt = CaptureLog(AssertStep.ReadLogLines(args)),
            },
        };
    }

    // Warnings and errors only, from the game's own in-memory buffer — the same one dev mode's log
    // window shows. Ordinary messages are dropped because the buffer is mostly startup chatter and
    // the judge's attention is worth more than completeness here; what matters is whether something
    // actually went wrong behind a picture that looks plausible.
    //
    // Read from Verse.Log rather than Player.log on disk: the file is owned by Unity and being
    // written concurrently, whereas this is already parsed, deduplicated (LogMessage.repeats) and
    // free of IO that could throw inside the tick loop.
    private static List<string> CaptureLog(int maxLines)
    {
        if (maxLines <= 0)
            return new List<string>();

        // Scoped to this scenario, not the whole session — see StepHelpers.MarkLogBaseline.
        List<string> lines = StepHelpers.MessagesSinceBaseline()
            .Where(m => m.type != LogMessageType.Message)
            .Select(Format)
            .ToList();

        // The tail, not the head: the interesting failure is the one nearest the step that just ran,
        // and a long run's buffer opens with startup noise.
        int skip = lines.Count > maxLines ? lines.Count - maxLines : 0;
        return lines.Skip(skip).ToList();
    }

    private static string Format(LogMessage message)
    {
        string prefix = message.type == LogMessageType.Error ? "ERROR" : "WARN";
        string repeats = message.repeats > 1 ? $" (x{message.repeats})" : "";
        return $"[{prefix}] {message.text}{repeats}";
    }
}
