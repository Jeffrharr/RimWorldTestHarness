using System.Collections.Generic;
using System.Text.Json;

namespace RimWorldTestHarness.Shared;

// The wire contract for the interactive "companion" channel: a client (the RimWorldDevMCP server,
// or a CLI, or a human dropping a file) sends LiveRequests, the in-game LiveCommandDriver replies
// with LiveResponses, publishes a LiveStatus heartbeat, and emits a Catalog of what's available in
// the currently-loaded game.
//
// These are plain DTOs with no transport assumptions baked in. Today the transport is a file queue
// (one JSON file per message); the contract is deliberately decoupled from that so a later move to
// HTTP/TCP reuses the exact same shapes and the exact same parsing on both the C# and Python sides
// — only the "how bytes move" layer changes. Same reasoning as Shared/ being netstandard2.0: this
// file compiles into the Mod (net481) and the Tests project (net8.0) unchanged.

// One dev-action command, client -> listener.
public sealed class LiveRequest
{
    public string Id { get; set; } = "";       // uuid; correlates the response back to this request
    public long Seq { get; set; }               // monotonic; gives the listener a stable FIFO order
    public string Action { get; set; } = "";    // a StepArgs.*Type, a native [DebugAction] name, or a control verb
    public Dictionary<string, string> Args { get; set; } = new();
    public int TimeoutMs { get; set; }          // advisory; the client owns the real timeout
}

// The reply to exactly one LiveRequest, correlated by Id.
public sealed class LiveResponse
{
    public string Id { get; set; } = "";
    public long Seq { get; set; }
    public bool Ok { get; set; }
    public string Action { get; set; } = "";
    // Free-form string->string so new actions add result keys without a schema change (e.g.
    // Probe -> {"value":"0.42"}, Screenshot -> {"path":"/abs/....png","width":"1920"}).
    public Dictionary<string, string> Result { get; set; } = new();
    public string? Error { get; set; }          // non-null iff Ok == false
    public long TicksGame { get; set; }         // game tick the action completed on
}

// Heartbeat the listener rewrites ~once/second, so a client can tell a live game is present and read
// coarse world state without issuing a command. A stale/missing file == no live game.
public sealed class LiveStatus
{
    public bool HarnessLoaded { get; set; }
    public bool MapLoaded { get; set; }
    public long TicksGame { get; set; }
    public int DayOfYear { get; set; }
    public float HourFloat { get; set; }
    public string TimeSpeed { get; set; } = "";
    public string? InFlight { get; set; }       // Id of the command currently executing, if any
    public long ProcessedSeq { get; set; }      // highest Seq the listener has finished
    public long UpdatedAtTick { get; set; }     // lets a client detect a frozen/hung listener
}

// The set of dev-actions and probes available in the CURRENTLY LOADED game — emitted on map load.
// This is the "knowledge of all available dev actions" surface, and it changes with the modset,
// which is why it's discovered from the running game rather than hardcoded.
public sealed class Catalog
{
    public List<CatalogAction> Actions { get; set; } = new();
    public List<CatalogProbe> Probes { get; set; } = new();
    public CatalogGame Game { get; set; } = new();
}

public sealed class CatalogAction
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";       // "native" (a RimWorld [DebugAction]) | "harness"
    public string ActionType { get; set; } = "";    // native DebugActionType (Action/ToolMap/...) or "" for harness verbs
    public bool Available { get; set; }             // native: IsAllowedInCurrentGameState; harness: true
    public bool Invokable { get; set; }             // false for tool-type actions needing a click target
    public List<CatalogArg> Args { get; set; } = new();
}

public sealed class CatalogArg
{
    public string Key { get; set; } = "";
    public string Type { get; set; } = "";          // "float" | "int" | "string"
    public string Desc { get; set; } = "";
}

public sealed class CatalogProbe
{
    public string Name { get; set; } = "";
    public string? Desc { get; set; }
    public string? Unit { get; set; }
}

public sealed class CatalogGame
{
    public string Version { get; set; } = "";
    public List<string> LoadedMods { get; set; } = new();
    public string MapName { get; set; } = "";
    public long Tick { get; set; }
}

// Shared serialization for every side of the channel. Writes compact PascalCase JSON (matching the
// existing ScenarioReport format), and reads case-insensitively so a client can be relaxed about
// casing (e.g. a Python client emitting camelCase still round-trips).
public static class LiveProtocol
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
