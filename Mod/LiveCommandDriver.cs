using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using RimWorldTestHarness.Mod.Features;
using RimWorldTestHarness.Mod.Probes;
using RimWorldTestHarness.Shared.Steps;
using RimWorldTestHarness.Shared;
using UnityEngine;
using Verse;

namespace RimWorldTestHarness.Mod;

// The interactive "companion" driver: while the live-channel setting is on and a map is loaded, it
// watches a fixed session directory for one-off dev-action commands, runs each on the main tick
// thread (via the same shared StepExecutor / DevActionCatalog the batch path uses), and writes back
// results, a live catalog of what's available, and a heartbeat. This is the half that makes "call
// any dev-action against whatever game is currently running" work.
//
// Design constraints that shape everything here:
//   * Never fights the batch ScenarioDriver — it bails immediately when a scenario is Active.
//   * Minimally invasive — it does NOT force DevMode and does NOT touch time speed except for an
//     explicit FastForward (which restores the prior speed when it finishes). Idle == zero writes.
//   * Everything crosses the boundary as pure-data JSON (Shared/LiveProtocol.cs) written atomically
//     (tmp + rename), so the client never reads a half-written file and a later HTTP transport could
//     reuse the exact same shapes.
public static class LiveCommandDriver
{
    // Idle work is throttled so an armed-but-unused channel costs almost nothing per frame.
    private const int ScanIntervalFrames = 10;      // how often to look for new requests when idle
    private const int HeartbeatIntervalFrames = 60; // ~1s at 60fps
    private const int ScreenshotWaitBudgetFrames = 120; // give a screenshot up to ~2s to hit disk

    // Which verbs this channel will run is now declared by each step itself (IStepSpec.LiveCallable)
    // rather than listed here, so a step cannot become live-callable by being added to one list and
    // forgotten in another. The default for anything new is NOT callable.
    //
    // The exclusions are unchanged and worth restating, because they are safety decisions rather
    // than gaps. Wait and Timelapse are batch-sequencing concepts with nothing to sequence here. The
    // scene-setup verbs (PlaceThings, SetTerrain, LookAt) and StartCondition mutate the colony —
    // PlaceThings spawns with WipeMode.Vanish, which destroys whatever occupies the footprint. This
    // channel points at a real player's running game and promises to stay minimally invasive (see
    // the header above); silently wiping part of someone's base over a file-drop channel would break
    // that. Scenario runs are where those verbs belong: they load a throwaway fixture and never save
    // it.
    private static HashSet<string> HarnessVerbs =>
        _harnessVerbs ??= new HashSet<string>(StepRegistry.LiveCallableTypes, StringComparer.Ordinal);

    private static HashSet<string>? _harnessVerbs;

    private static string? _sessionDir;
    private static bool _dirsReady;
    private static int _catalogMapId = int.MinValue;
    private static int _lastHeartbeatFrame = -99999;
    private static int _lastScanFrame = -99999;
    private static long _processedSeq;

    // In-flight command awaiting a post-effect wait (screenshot flush, or FastForward's tick target).
    private static LiveRequest? _inFlight;
    private static StepOutcome? _inFlightOutcome;
    private static int _flushFramesRemaining;
    private static int _screenshotWaitBudget;
    private static bool _waitingFastForward;
    private static int _fastForwardTarget;
    private static TimeSpeed _preFastForwardSpeed;

    // Called every frame from Patch_DriveScenario. Cheap no-op unless the channel is armed and a game
    // is actually being played.
    public static void Tick()
    {
        if (ScenarioDriver.Active)
            return; // batch owns the game exclusively
        if (!HarnessMain.LiveChannelEnabled)
            return;
        if (LongEventHandler.ShouldWaitForEvent)
            return;

        // ProgramState as well as a non-null map: CurrentMap goes non-null partway through
        // Game.InitNewGame/LoadGame, before FinalizeInit finishes, and touching the map in that window
        // corrupts tick state (see ScenarioDriver.ReadyToRun for the full write-up).
        if (Current.ProgramState != ProgramState.Playing)
            return;

        Map map = Find.CurrentMap;
        if (map == null)
            return;

        try
        {
            Pump(map);
        }
        catch (Exception ex)
        {
            // A malformed command or transient IO must never take down the frame.
            Log.Warning($"RWTH live channel: {ex}");
        }
    }

    private static void Pump(Map map)
    {
        EnsureDirs();
        MaybeEmitCatalog(map);
        MaybeHeartbeat(map);

        if (ResolveInFlight(map))
            return; // still waiting on the current command; don't start another

        LiveRequest? req = TakeNextRequest();
        if (req != null)
            ExecuteRequest(req, map);
    }

    // ----- session directory --------------------------------------------------------------------

    private static string SessionDir()
    {
        if (_sessionDir != null)
            return _sessionDir;

        // Mirror the XDG cache convention the Python client uses so both derive the SAME path with no
        // handshake — that's what lets a normally-launched game (no env vars) still be reachable.
        string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? "";
        if (string.IsNullOrEmpty(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        _sessionDir = Path.Combine(cache, "rimworld-dev-mcp", "live");
        return _sessionDir;
    }

    private static string RequestsDir() => Path.Combine(SessionDir(), "requests");
    private static string ResponsesDir() => Path.Combine(SessionDir(), "responses");
    private static string ScreenshotsDir() => Path.Combine(SessionDir(), "screenshots");

    private static void EnsureDirs()
    {
        if (_dirsReady)
            return;
        Directory.CreateDirectory(RequestsDir());
        Directory.CreateDirectory(ResponsesDir());
        Directory.CreateDirectory(ScreenshotsDir());
        _dirsReady = true;
    }

    // ----- catalog + heartbeat ------------------------------------------------------------------

    private static void MaybeEmitCatalog(Map map)
    {
        if (map.uniqueID == _catalogMapId)
            return;
        // Map changed (or first emit) — the loaded modset / dev-action set may differ, so rebuild.
        DevActionCatalog.Invalidate();
        EmitCatalog(map);
        _catalogMapId = map.uniqueID;
    }

    private static void EmitCatalog(Map map)
    {
        Catalog catalog = new();
        AddHarnessActions(catalog);
        DevActionCatalog.PopulateCatalog(catalog);
        AddProbes(catalog);
        catalog.Game = BuildGame(map);
        AtomicWrite(Path.Combine(SessionDir(), "catalog.json"), LiveProtocol.Serialize(catalog));
    }

    private static void AddHarnessActions(Catalog catalog)
    {
        catalog.Actions.Add(HarnessAction(StepArgs.SetTileType, Arg(StepArgs.SetTileLatitude, "float", "degrees, -90..90")));
        catalog.Actions.Add(HarnessAction(StepArgs.SetSeasonType, Arg(StepArgs.SetSeasonDayOfYear, "int", "0..59")));
        catalog.Actions.Add(HarnessAction(StepArgs.SetTimeType, Arg(StepArgs.SetTimeHour, "float", "0..24")));
        catalog.Actions.Add(HarnessAction(StepArgs.FastForwardType, Arg(StepArgs.FastForwardTicks, "int", "game ticks to advance")));
        catalog.Actions.Add(HarnessAction(StepArgs.ProbeType, Arg(StepArgs.ProbeName, "string", "a registered probe name")));
        catalog.Actions.Add(HarnessAction(StepArgs.ScreenshotType, Arg(StepArgs.ScreenshotFileName, "string", "optional output PNG name")));
        catalog.Actions.Add(HarnessAction(StepArgs.SetFeatureType,
            Arg(StepArgs.SetFeatureName, "string", "one of: " + string.Join(", ", FeatureRegistry.Names)),
            Arg(StepArgs.SetFeatureEnabled, "bool", "true/false")));
    }

    private static CatalogAction HarnessAction(string name, params CatalogArg[] args)
        => new()
        {
            Name = name,
            Category = "RimWorldTestHarness",
            Source = "harness",
            ActionType = "",
            Available = true,
            Invokable = true,
            Args = new List<CatalogArg>(args),
        };

    private static CatalogArg Arg(string key, string type, string desc)
        => new() { Key = key, Type = type, Desc = desc };

    private static void AddProbes(Catalog catalog)
    {
        foreach (IProbe probe in ProbeRegistry.All)
        {
            CatalogProbe entry = new() { Name = probe.Name };
            if (probe is IProbeMetadata meta)
            {
                entry.Desc = meta.Description;
                entry.Unit = meta.Unit;
            }
            catalog.Probes.Add(entry);
        }
    }

    private static CatalogGame BuildGame(Map map)
    {
        CatalogGame game = new()
        {
            Version = "",                       // Python client reads the install's Version.txt if it needs it
            MapName = map.Parent?.LabelCap ?? "",
            Tick = Find.TickManager.TicksGame,
        };
        foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            game.LoadedMods.Add(mod.PackageId);
        return game;
    }

    private static void MaybeHeartbeat(Map map)
    {
        if (Time.frameCount - _lastHeartbeatFrame < HeartbeatIntervalFrames)
            return;
        _lastHeartbeatFrame = Time.frameCount;

        float longitude = LongitudeOf(map);
        int ticksAbs = Find.TickManager.TicksAbs;
        LiveStatus status = new()
        {
            HarnessLoaded = true,
            MapLoaded = true,
            TicksGame = Find.TickManager.TicksGame,
            DayOfYear = GenDate.DayOfYear(ticksAbs, longitude),
            HourFloat = GenDate.HourFloat(ticksAbs, longitude),
            TimeSpeed = Find.TickManager.CurTimeSpeed.ToString(),
            InFlight = _inFlight?.Id,
            ProcessedSeq = _processedSeq,
            UpdatedAtTick = Find.TickManager.TicksGame,
        };
        AtomicWrite(Path.Combine(SessionDir(), "status.json"), LiveProtocol.Serialize(status));
    }

    // ----- request intake -----------------------------------------------------------------------

    private static LiveRequest? TakeNextRequest()
    {
        if (Time.frameCount - _lastScanFrame < ScanIntervalFrames)
            return null;
        _lastScanFrame = Time.frameCount;

        string[] files;
        try
        {
            files = Directory.GetFiles(RequestsDir(), "*.json");
        }
        catch
        {
            return null;
        }
        if (files.Length == 0)
            return null;

        LiveRequest? best = null;
        string? bestFile = null;
        foreach (string file in files)
        {
            if (TryReadRequest(file, out LiveRequest? req) && IsEarlier(req!, best))
            {
                best = req;
                bestFile = file;
            }
        }

        // Claim the chosen request by deleting its file so it's never picked twice; the response goes
        // out under the request's Id, which the client correlates and then cleans up.
        if (bestFile != null)
            TryDelete(bestFile);
        return best;
    }

    private static bool IsEarlier(LiveRequest candidate, LiveRequest? current)
        => current == null || candidate.Seq < current.Seq;

    private static bool TryReadRequest(string file, out LiveRequest? req)
    {
        req = null;
        try
        {
            LiveRequest? parsed = LiveProtocol.Deserialize<LiveRequest>(File.ReadAllText(file));
            if (parsed == null)
                return false;
            req = parsed;
            return true;
        }
        catch
        {
            return false; // half-written or malformed — skip it, it'll be retried or replaced
        }
    }

    // ----- execution ----------------------------------------------------------------------------

    private static void ExecuteRequest(LiveRequest req, Map map)
    {
        if (req.Action == "Ping")
        {
            RespondOk(req, new Dictionary<string, string> { ["pong"] = "1" });
            return;
        }

        if (HarnessVerbs.Contains(req.Action))
        {
            ExecuteHarnessVerb(req, map);
            return;
        }

        // Anything else is treated as a native RimWorld [DebugAction] name (the generic escape hatch).
        string? error = DevActionCatalog.Invoke(req.Action);
        if (error != null)
            RespondError(req, error);
        else
            RespondOk(req, new Dictionary<string, string> { ["invoked"] = req.Action });
    }

    private static void ExecuteHarnessVerb(LiveRequest req, Map map)
    {
        IReadOnlyDictionary<string, string> args = ArgsFor(req);
        if (req.Action == StepArgs.FastForwardType)
            _preFastForwardSpeed = Find.TickManager.CurTimeSpeed;

        StepContext ctx = new(map, name => Path.Combine(ScreenshotsDir(), name));
        StepOutcome outcome = StepExecutor.Execute(req.Action, args, ctx);
        BeginOutcome(req, outcome);
    }

    // Live screenshots default to a seq-based name under the session dir when the client doesn't name
    // one (batch always names them; live callers usually don't care).
    private static IReadOnlyDictionary<string, string> ArgsFor(LiveRequest req)
    {
        bool needsDefaultName = req.Action == StepArgs.ScreenshotType
            && !req.Args.ContainsKey(StepArgs.ScreenshotFileName);
        if (!needsDefaultName)
            return req.Args;

        Dictionary<string, string> copy = new(req.Args)
        {
            [StepArgs.ScreenshotFileName] = $"shot-{req.Seq}.png",
        };
        return copy;
    }

    private static void BeginOutcome(LiveRequest req, StepOutcome outcome)
    {
        if (outcome.Error != null)
        {
            RespondError(req, outcome.Error);
            return;
        }

        if (outcome.ForcedLatitude is float latitude)
            HarnessRuntime.ForcedLatitude = latitude;

        if (outcome.WaitFastForward)
        {
            _inFlight = req;
            _inFlightOutcome = outcome;
            _waitingFastForward = true;
            _fastForwardTarget = outcome.FastForwardTargetTicksGame;
            return;
        }

        if (outcome.WaitFrames > 0)
        {
            _inFlight = req;
            _inFlightOutcome = outcome;
            _flushFramesRemaining = outcome.WaitFrames;
            _screenshotWaitBudget = ScreenshotWaitBudgetFrames;
            return;
        }

        RespondOk(req, ResultFor(req.Action, outcome));
    }

    // Advances any in-flight command's wait. Returns true while still waiting (caller should not start
    // a new command), false when nothing is in flight or it just resolved.
    private static bool ResolveInFlight(Map map)
    {
        if (_inFlight == null)
            return false;

        if (_waitingFastForward)
            return ResolveFastForward();

        return ResolveScreenshot();
    }

    private static bool ResolveFastForward()
    {
        if (Find.TickManager.TicksGame < _fastForwardTarget)
            return true;

        // Explicit action is done — put the clock back the way we found it (we only ever touch time
        // speed for an explicit FastForward).
        Find.TickManager.CurTimeSpeed = _preFastForwardSpeed;
        _waitingFastForward = false;

        LiveRequest req = _inFlight!;
        RespondOk(req, ResultFor(req.Action, _inFlightOutcome!));
        return false;
    }

    private static bool ResolveScreenshot()
    {
        if (_flushFramesRemaining > 0)
        {
            _flushFramesRemaining--;
            return true;
        }

        string path = _inFlightOutcome!.ScreenshotPath!;
        if (!FileReady(path))
        {
            if (_screenshotWaitBudget-- > 0)
                return true;
            RespondError(_inFlight!, "screenshot did not appear on disk in time");
            return false;
        }

        RespondOk(_inFlight!, ResultFor(_inFlight!.Action, _inFlightOutcome!));
        return false;
    }

    private static Dictionary<string, string> ResultFor(string action, StepOutcome outcome)
    {
        Dictionary<string, string> result = new();
        if (outcome.ProbeValue is float value)
            result["value"] = value.ToString("R");
        if (outcome.ScreenshotPath != null)
            result["path"] = outcome.ScreenshotPath;
        if (outcome.ForcedLatitude is float latitude)
            result["latitude"] = latitude.ToString("R");
        return result;
    }

    // ----- responses ----------------------------------------------------------------------------

    private static void RespondOk(LiveRequest req, Dictionary<string, string> result)
        => Respond(new LiveResponse
        {
            Id = req.Id,
            Seq = req.Seq,
            Ok = true,
            Action = req.Action,
            Result = result,
            TicksGame = Find.TickManager.TicksGame,
        });

    private static void RespondError(LiveRequest req, string error)
        => Respond(new LiveResponse
        {
            Id = req.Id,
            Seq = req.Seq,
            Ok = false,
            Action = req.Action,
            Error = error,
            TicksGame = Find.TickManager.TicksGame,
        });

    private static void Respond(LiveResponse resp)
    {
        AtomicWrite(Path.Combine(ResponsesDir(), resp.Id + ".json"), LiveProtocol.Serialize(resp));
        _processedSeq = Math.Max(_processedSeq, resp.Seq);
        ClearInFlight();
    }

    private static void ClearInFlight()
    {
        _inFlight = null;
        _inFlightOutcome = null;
        _flushFramesRemaining = 0;
        _screenshotWaitBudget = 0;
        _waitingFastForward = false;
    }

    // ----- small helpers ------------------------------------------------------------------------

    private static float LongitudeOf(Map map) => Find.WorldGrid.LongLatOf(map.Tile).x;

    private static bool FileReady(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    // Write-then-rename so a reader never sees a partial file. File.Replace maps to an atomic rename
    // on the same filesystem; fall back to delete+move if the platform refuses it.
    private static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!File.Exists(path))
        {
            File.Move(tmp, path);
            return;
        }
        try
        {
            File.Replace(tmp, path, null);
        }
        catch
        {
            File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
