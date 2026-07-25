using System.Collections.Generic;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Mod.Steps.BuiltIn;

public sealed class ProbeAction : IStepAction
{
    public string Type => StepArgs.ProbeType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string probeName = args[StepArgs.ProbeName];
        if (!ProbeRegistry.TryGet(probeName, out IProbe? probe) || probe == null)
            return StepOutcome.Fail($"No probe registered named '{probeName}'");

        return new StepOutcome { ProbeValue = probe.Read(ctx.Map) };
    }
}

public sealed class ScreenshotAction : IStepAction
{
    public string Type => StepArgs.ScreenshotType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string fileName = args[StepArgs.ScreenshotFileName];
        string path = ctx.ResolveScreenshotPath(fileName);
        // Hidden by default: a batch run forces DevMode, so every frame would otherwise carry the
        // dev toolbar on top of the colonist bar, alerts, letters and bottom bar — noise in a
        // single screenshot, and 24x the noise in a timelapse. Opt back in with hideUi=false when
        // the UI itself is what's under test.
        bool hideUi = StepHelpers.ParseBool(args, StepArgs.ScreenshotHideUi, defaultValue: true);
        // Shared capture core — the same one the [DebugAction] dev-menu entry uses.
        HarnessDebugActions.CaptureScreenshotTo(path, hideUi);
        // CaptureScreenshot writes asynchronously over the next few frames — the driver waits
        // WaitFrames before the next command (or, for live mode, before returning the PNG).
        return new StepOutcome { ScreenshotPath = path, WaitFrames = 5 };
    }
}

// Flips a named runtime feature flag in the mod under test via FeatureRegistry (which the mod's
// dev-only bridge assembly populated at startup). Takes no game state — the effect is entirely in
// the target mod's static flag — so a scenario can bracket a Screenshot pair with off/on SetFeature
// steps for an A/B visual diff. An unregistered name becomes a scenario error rather than a crash,
// matching how Probe handles an unknown probe name.
public sealed class SetFeatureAction : IStepAction
{
    public string Type => StepArgs.SetFeatureType;

    public StepOutcome Execute(IReadOnlyDictionary<string, string> args, StepContext ctx)
    {
        string name = args[StepArgs.SetFeatureName];
        bool enabled = bool.Parse(args[StepArgs.SetFeatureEnabled]);
        if (!FeatureRegistry.TrySet(name, enabled))
            return StepOutcome.Fail($"No feature registered named '{name}'");

        return new StepOutcome();
    }
}
