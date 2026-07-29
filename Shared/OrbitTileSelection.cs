using System;
using System.Collections.Generic;

namespace RimWorldTestHarness.Shared;

// Picking which planet-layer tile a scenario's requested lat/long resolves to.
//
// WHY THIS IS ITS OWN PURE FILE. The orbit layer is an icosphere subdivided a fixed number of times
// (five, per Odyssey's `Orbit` PlanetLayerSettingsDef), so its tiles land where the subdivision puts
// them and NOT where a scenario asks. "Latitude 45" therefore always means "the orbit tile nearest
// latitude 45", and how near it actually got is a number the run has to report rather than round off:
// with stationary orbits the platform's lat/long fully determines its day length and sun path, so a
// silent few degrees of drift is a silently different answer for every lighting probe on that map.
//
// The layer is also only generated across the world's view angle (IcosahedronGenerator takes it as a
// cap), so on a small-coverage world whole latitude bands simply do not exist. That is the case this
// file exists to make loud: it returns how far off the best tile was, and the caller fails the step
// when that exceeds what the scenario said it would tolerate.
//
// Kept game-free so both the selection rule and the tolerance check are unit-testable offline; the
// action does nothing but read tile centres out of the live layer and hand them here.
public static class OrbitTileSelection
{
    // Degrees the chosen tile may sit from the requested lat/long before the step fails. Five is a bit
    // over one tile's width at the orbit layer's subdivision level (≈2°), so a scenario asking for a
    // latitude the layer covers always resolves, while one asking for a latitude outside the generated
    // cap fails instead of quietly landing in a band it did not ask for.
    public const float DefaultMaxOffsetDegrees = 5f;

    // Angular separation between two points on a sphere, in degrees (the spherical law of cosines).
    // Clamped before Acos because accumulated float error can push the dot product a hair outside
    // [-1, 1], where Acos returns NaN — and a NaN would compare false against every candidate, so the
    // whole selection would silently return "no tile" rather than the nearest one.
    public static double SeparationDegrees(double latA, double lonA, double latB, double lonB)
    {
        double la = latA * Math.PI / 180.0;
        double lb = latB * Math.PI / 180.0;
        double dLon = (lonB - lonA) * Math.PI / 180.0;

        double cos = Math.Sin(la) * Math.Sin(lb) + Math.Cos(la) * Math.Cos(lb) * Math.Cos(dLon);
        return Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))) * 180.0 / Math.PI;
    }

    // The index of the tile nearest the request, or -1 when there are no tiles at all.
    //
    // `targetLon == null` means the scenario named a latitude only, which is the common case and the
    // one worth supporting well: longitude does not change the sun's elevation over a tile, only the
    // local-time offset that SetTime/SetSeason already read off the real tile. Ignoring it widens the
    // pool of acceptable tiles enormously on a small-coverage world, where a full lat/long pair may
    // simply not have been generated.
    //
    // Ties break toward the lowest index, so the same world resolves the same tile on every run. That
    // determinism is the point: a probe pinned against tile N is only pinned if the next run picks
    // tile N too.
    public static int PickNearest(
        IReadOnlyList<float> tileLats,
        IReadOnlyList<float> tileLons,
        float targetLat,
        float? targetLon,
        out double offsetDegrees)
    {
        offsetDegrees = double.PositiveInfinity;
        if (tileLats == null || tileLons == null || tileLats.Count == 0)
            return -1;

        int best = -1;
        double bestOffset = double.PositiveInfinity;

        for (int i = 0; i < tileLats.Count; i++)
        {
            double offset = OffsetOf(tileLats[i], tileLons[i], targetLat, targetLon);
            if (offset < bestOffset)
            {
                bestOffset = offset;
                best = i;
            }
        }

        offsetDegrees = bestOffset;
        return best;
    }

    private static double OffsetOf(float tileLat, float tileLon, float targetLat, float? targetLon) =>
        targetLon is float lon
            ? SeparationDegrees(tileLat, tileLon, targetLat, lon)
            : Math.Abs(tileLat - targetLat);
}
