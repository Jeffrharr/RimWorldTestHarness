using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// Persisted mod settings. The live companion channel is OFF by default: it's a dev tool, and the
// mod otherwise does nothing in normal play (the batch path is env-var gated). A player who leaves
// this mod enabled sees no behaviour until they deliberately tick the box.
public sealed class HarnessSettings : ModSettings
{
    public bool enableLiveChannel;

    public override void ExposeData()
    {
        Scribe_Values.Look(ref enableLiveChannel, "enableLiveChannel", defaultValue: false);
        base.ExposeData();
    }
}

// The Verse.Mod subclass exists purely to give RimWorld a settings entry (the checkbox that arms the
// live channel) and a static handle other code reads. It's separate from HarnessMod
// ([StaticConstructorOnStartup], which does Harmony.PatchAll + the batch bootstrap) — the two are
// distinct RimWorld entry points and coexist fine.
public sealed class HarnessMain : Verse.Mod
{
    public static HarnessSettings? Settings { get; private set; }

    // Single place the LiveCommandDriver checks each frame. Null-safe so a construction-order hiccup
    // just reads as "off" rather than throwing inside the tick loop.
    public static bool LiveChannelEnabled => Settings?.enableLiveChannel ?? false;

    public HarnessMain(ModContentPack content) : base(content)
    {
        Settings = GetSettings<HarnessSettings>();
    }

    public override string SettingsCategory() => "RimWorld Test Harness";

    public override void DoSettingsWindowContents(Rect inRect)
    {
        HarnessSettings settings = Settings ??= GetSettings<HarnessSettings>();

        Listing_Standard listing = new();
        listing.Begin(inRect);
        listing.CheckboxLabeled(
            "Enable Claude live dev channel",
            ref settings.enableLiveChannel,
            "When on, this mod watches ~/.cache/rimworld-dev-mcp/live for dev-action commands from " +
            "the RimWorldDevMCP server (or a CLI) and replies with results/screenshots. Leave off " +
            "for normal play — it does nothing while unchecked.");
        listing.End();
    }
}
