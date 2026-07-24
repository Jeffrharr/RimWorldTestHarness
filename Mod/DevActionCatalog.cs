using System;
using System.Collections.Generic;
using System.Reflection;
using LudeonTK;
using RimWorldTestHarness.Shared;
using Verse;

namespace RimWorldTestHarness.Mod;

// Discovers RimWorld's own dev-action registry by reflection, so the live catalog reflects the
// dev-actions actually present in the CURRENTLY LOADED game — which changes with the modset, hence
// why it's discovered at runtime rather than hardcoded. Also invokes a native dev-action by name for
// the live channel's generic dev_action escape hatch.
//
// Scope (the plan's "honest limits"): only DebugActionType.Action entries are zero-arg and directly
// invokable headlessly. Tool-type actions (ToolMap/ToolWorld/ToolMapForPawns) arm a click-to-target
// tool; they're enumerated-but-not-invokable here, and supplying targets is future work.
public static class DevActionCatalog
{
    private sealed class NativeAction
    {
        public string Name = "";
        public string Category = "";
        public DebugActionType ActionType;
        public MethodInfo Method = null!;
        public DebugActionAttribute Attr = null!;

        // Directly callable over the channel only if it's a zero-arg Action (no click target, no
        // parameters we'd have to synthesize).
        public bool Invokable => ActionType == DebugActionType.Action && Method.GetParameters().Length == 0;
    }

    // Reflection over every game/mod type is not free, so build once and reuse. Invalidate() forces a
    // rebuild (e.g. on a fresh map load, in case a hot-loaded assembly changed the set).
    private static List<NativeAction>? _cache;

    public static void Invalidate() => _cache = null;

    private static List<NativeAction> All()
    {
        if (_cache != null)
            return _cache;

        List<NativeAction> found = new();
        foreach (Type type in GenTypes.AllTypes)
            CollectFromType(type, found);
        _cache = found;
        return found;
    }

    private static void CollectFromType(Type type, List<NativeAction> into)
    {
        MethodInfo[] methods;
        try
        {
            // DebugAction methods are frequently non-public static, so include NonPublic.
            methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        }
        catch
        {
            // Some types throw on reflection (unresolved generic params, missing deps); skip them —
            // the game itself tolerates the same and just omits them from the menu.
            return;
        }

        foreach (MethodInfo method in methods)
            AddIfDebugAction(method, into);
    }

    private static void AddIfDebugAction(MethodInfo method, List<NativeAction> into)
    {
        DebugActionAttribute? attr = method.GetCustomAttribute<DebugActionAttribute>();
        if (attr == null)
            return;

        into.Add(new NativeAction
        {
            // The menu shows attr.name, falling back to the method name when it's null — mirror that
            // so our catalog names match what a developer sees in-game.
            Name = string.IsNullOrEmpty(attr.name) ? method.Name : attr.name,
            Category = attr.category,
            ActionType = attr.actionType,
            Method = method,
            Attr = attr,
        });
    }

    // Adds every discovered native dev-action to the catalog, flagging current availability and
    // whether it's directly invokable.
    public static void PopulateCatalog(Catalog catalog)
    {
        foreach (NativeAction a in All())
        {
            catalog.Actions.Add(new CatalogAction
            {
                Name = a.Name,
                Category = a.Category,
                Source = "native",
                ActionType = a.ActionType.ToString(),
                Available = IsAllowed(a.Attr),
                Invokable = a.Invokable,
            });
        }
    }

    // Invokes a native Action-type dev-action by display name. Returns null on success, or an error
    // string. Tool-type / unknown / not-currently-allowed actions are refused cleanly rather than
    // half-invoked. Note: some Action-type entries open a UI sub-dialog rather than doing something
    // headless — we can't distinguish those, so the invoke "succeeds" but may just have popped a
    // window; the catalog's category/name is the client's cue.
    public static string? Invoke(string name)
    {
        NativeAction? match = FindInvokable(name);
        if (match == null)
            return $"No invokable native dev-action named '{name}' " +
                   "(unknown, or a tool-type action that needs a click target, which isn't supported yet)";
        if (!IsAllowed(match.Attr))
            return $"Native dev-action '{name}' isn't allowed in the current game state";

        match.Method.Invoke(null, null);
        return null;
    }

    private static NativeAction? FindInvokable(string name)
    {
        foreach (NativeAction a in All())
        {
            if (a.Name == name && a.Invokable)
                return a;
        }
        return null;
    }

    private static bool IsAllowed(DebugActionAttribute attr)
    {
        try
        {
            return attr.IsAllowedInCurrentGameState;
        }
        catch
        {
            return false;
        }
    }
}
