using System;
using RimWorldTestHarness.Mod;
using RimWorldTestHarness.Shared;

namespace RimWorldTestHarness.Tests;

// Covers the invalidation path a mod's per-tile cache depends on. Worth testing offline rather than
// only through a live suite run: the failure this guards against is silent — a stale cache returns a
// plausible number, so the scenario goes green with the wrong answer, which is exactly the class of
// bug a live run is least likely to catch.
[TestFixture]
public sealed class WorldOverrideHookTests
{
    // Both the registry and HarnessRuntime.ForcedLatitude are static, so a test inheriting either
    // from its predecessor would pass or fail on ordering.
    [SetUp]
    public void Reset()
    {
        WorldOverrideHookRegistry.ClearForTesting();
        HarnessRuntime.ForcedLatitude = null;
        WorldOverrideHookRegistry.ClearForTesting();
    }

    [Test]
    public void SettingLatitude_FiresRegisteredHook()
    {
        int fired = 0;
        WorldOverrideHookRegistry.Register(() => fired++);

        HarnessRuntime.ForcedLatitude = 45f;

        Assert.That(fired, Is.EqualTo(1), "a latitude change must invalidate mod caches keyed by tile");
    }

    [Test]
    public void ClearingLatitude_FiresHook()
    {
        HarnessRuntime.ForcedLatitude = 45f;
        int fired = 0;
        WorldOverrideHookRegistry.Register(() => fired++);

        // What WorldStateReset does between scenarios. The reset direction matters as much as the set:
        // a scenario that follows a latitude-forcing one and reads the tile's REAL latitude would
        // otherwise still get the forced tile's cached half-day.
        HarnessRuntime.ForcedLatitude = null;

        Assert.That(fired, Is.EqualTo(1));
    }

    // The case the whole thing exists for: consecutive scenarios at different latitudes on one boot.
    [Test]
    public void ChangingBetweenTwoLatitudes_FiresForEach()
    {
        int fired = 0;
        WorldOverrideHookRegistry.Register(() => fired++);

        HarnessRuntime.ForcedLatitude = 20f;
        HarnessRuntime.ForcedLatitude = 45f;

        Assert.That(fired, Is.EqualTo(2));
    }

    [Test]
    public void WritingTheSameLatitude_DoesNotFire()
    {
        HarnessRuntime.ForcedLatitude = 45f;
        int fired = 0;
        WorldOverrideHookRegistry.Register(() => fired++);

        HarnessRuntime.ForcedLatitude = 45f;

        Assert.That(fired, Is.Zero, "a no-op write must not cost a cache flush");
    }

    // WorldStateReset nulls the latitude between every scenario in a suite, including the majority
    // that never set one. Firing on those would scale the flush count with scenario count.
    [Test]
    public void ClearingAnAlreadyNullLatitude_DoesNotFire()
    {
        int fired = 0;
        WorldOverrideHookRegistry.Register(() => fired++);

        HarnessRuntime.ForcedLatitude = null;

        Assert.That(fired, Is.Zero);
    }

    [Test]
    public void EveryRegisteredHookFires()
    {
        int a = 0, b = 0;
        WorldOverrideHookRegistry.Register(() => a++);
        WorldOverrideHookRegistry.Register(() => b++);

        HarnessRuntime.ForcedLatitude = 60f;

        Assert.That(a, Is.EqualTo(1));
        Assert.That(b, Is.EqualTo(1));
    }

    // A mod's cache flush is somebody else's code on a path that runs mid-scenario, so a throw must
    // not escape into the setter — and must not cost the hooks registered after it either.
    [Test]
    public void AThrowingHook_IsContainedAndLaterHooksStillRun()
    {
        int later = 0;
        string? reported = null;
        WorldOverrideHookRegistry.ErrorSink = m => reported = m;
        WorldOverrideHookRegistry.Register(() => throw new InvalidOperationException("boom"));
        WorldOverrideHookRegistry.Register(() => later++);

        Assert.DoesNotThrow(() => HarnessRuntime.ForcedLatitude = 30f);
        Assert.That(later, Is.EqualTo(1), "one bad hook must not suppress the rest");
        Assert.That(reported, Does.Contain("boom"));
    }

    // The value must be committed before the hooks run: a hook that reads back the latitude it is
    // being told about would otherwise see the old one and re-cache exactly the stale answer.
    [Test]
    public void HookSeesTheNewValue_NotTheOldOne()
    {
        HarnessRuntime.ForcedLatitude = 20f;
        float? seen = null;
        WorldOverrideHookRegistry.Register(() => seen = HarnessRuntime.ForcedLatitude);

        HarnessRuntime.ForcedLatitude = 45f;

        Assert.That(seen, Is.EqualTo(45f));
    }
}
