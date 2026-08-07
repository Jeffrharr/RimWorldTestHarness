#!/usr/bin/env python3
"""Runner/frame_delta.py — how far apart are two frames, in units a reviewer can act on.

WHY THIS EXISTS
---------------
The harness could always capture a screenshot, and it could always assert a NUMBER a mod computed
in-process (Probe). It could never assert anything about the PIXELS, which is the failure mode the
whole repo was started over: CelestialLighting #15 shipped with unit tests and a numeric probe both
green while the effect rendered nothing at all. A formula returning the right value and that value
reaching the screen are two claims, and only one of them was ever checked.

Two frames from ONE boot are the tractable version of that question. The scene is random per boot —
colony layout, weather, pawns, HUD — so no absolute pixel value is reproducible. But capture a
frame, toggle one feature, capture again, and everything except the feature under test is identical
by construction. The DIFFERENCE is stable even though neither frame is. That is the measurement
this module makes, and it is the one issue #7 calls `expectDelta`.

WHY PER-PIXEL ΔE AND NOT CHANNEL MEANS
--------------------------------------
A channel mean has twice now reported a subsystem working while it was invisible on screen. Means
cancel: a change that lifts red on lit ground and drops it in shadow reads as nothing at all. The
median per-pixel CIELAB distance tracks "would a player notice", and the thresholds are the
standard ones — under 1 imperceptible, 1–2 on close inspection, 2+ at a glance, 5+ obvious.

The median specifically, not the mean, because a frame is mostly background: an effect that
transforms a fifth of the image has a mean dragged toward zero by the four fifths it did not touch,
while the median says what the typical pixel did. p90/p99 are reported alongside so a genuinely
localized effect (which SHOULD have a low median and a high tail) is distinguishable from no effect
at all.

WHY A HUE VERDICT TOO, AND NOT ONLY A MAGNITUDE
-----------------------------------------------
A large ΔE that stayed on the Planckian locus means the frame merely got warmer or cooler. For a
subsystem whose claim is a HUE, that is a failure wearing a good number. So the report also carries
Duv — the signed CIE 1960 distance from the locus, positive on the green side, negative on the
purple/magenta side — and the nearest correlated colour temperature. A "this should go purple"
assertion has to cross into negative Duv; a "this should go warmer" one only has to drop the CCT.

DEPENDENCIES
------------
ffmpeg and the standard library, nothing else. Neither numpy nor Pillow is a dependency of this
repo and neither should become one for a post-processing helper: run_test.sh already shells out to
ffmpeg to stitch timelapses, so the decoder is a tool the runner has by construction. The pure
maths below is consequently hand-rolled, which is fine — it is also what makes every decision in
this file unit-testable against synthetic pixel buffers with no game, no ffmpeg and no run. See
Tests/runner/test_frame_delta.py.

    python3 frame_delta.py BEFORE.png AFTER.png [--region full|X,Y,W,H] [--stride N] [--json]
"""

import argparse
import json
import math
import subprocess

# ---------------------------------------------------------------------------------------------
# Region selection.
# ---------------------------------------------------------------------------------------------
# Whole-frame is the default, and for the shape this module was built for — one boot, toggle one
# feature, compare two frames — it is the RIGHT default rather than merely the easy one. Everything
# outside the effect is byte-identical by construction, so the untouched majority of the frame
# contributes exact zeros; it dilutes the mean (which is why the median is the headline) but it
# cannot invent a difference.
#
# A region exists for the case where a scenario legitimately changes the scene between the two
# captures — different terrain painted, a thing spawned, the camera moved — because then "everything
# else is identical" stops being true and a whole-frame number is partly a statement about the
# change you did not care about. Restricting to a rect puts the measurement back on the effect.
#
# Deliberately just a rect. Content-derived masks ("the shadowed pixels", "the sky") were in the
# original sketch for issue #7 and are omitted: each one is a heuristic that can be wrong in a way
# the number does not reveal, and a wrong mask is exactly how a metric quietly stops measuring what
# its name says. A rect is checkable by looking at the frame.
FULL_REGION = "full"


class Region:
    """A sampling rectangle in pixels, origin top-left."""

    def __init__(self, x, y, width, height):
        self.x = x
        self.y = y
        self.width = width
        self.height = height

    def as_dict(self):
        return {"X": self.x, "Y": self.y, "Width": self.width, "Height": self.height}

    def __eq__(self, other):
        return isinstance(other, Region) and self.as_dict() == other.as_dict()

    def __repr__(self):
        return f"Region({self.x},{self.y},{self.width},{self.height})"


def parse_region(raw, frame_width, frame_height):
    """'full' or 'X,Y,W,H' -> Region, validated against the frame it will be applied to.

    Out-of-bounds is an error rather than a clamp. A clamped rect still produces a number, and a
    number produced from a rect other than the one the scenario asked for is the failure this whole
    module is trying to make impossible.
    """
    if raw is None or str(raw).strip() == "" or str(raw).strip().lower() == FULL_REGION:
        return Region(0, 0, frame_width, frame_height)

    parts = [p.strip() for p in str(raw).split(",")]
    if len(parts) != 4:
        raise ValueError(f"region must be '{FULL_REGION}' or 'X,Y,W,H' (got {raw!r})")

    try:
        x, y, width, height = (int(p) for p in parts)
    except ValueError:
        raise ValueError(f"region bounds must be whole pixels (got {raw!r})")

    if width <= 0 or height <= 0:
        raise ValueError(f"region must have positive width and height (got {raw!r})")
    if x < 0 or y < 0:
        raise ValueError(f"region origin must be inside the frame (got {raw!r})")
    if x + width > frame_width or y + height > frame_height:
        raise ValueError(
            f"region {raw!r} runs past the {frame_width}x{frame_height} frame")

    return Region(x, y, width, height)


# ---------------------------------------------------------------------------------------------
# Decoding. ffmpeg is the one image tool the runner already depends on, so shell out to it for raw
# rgb24 rather than writing a PNG decoder.
# ---------------------------------------------------------------------------------------------


def decode(path):
    """(width, height, rgb24 bytes) for one image file."""
    probe = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0",
         "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", path],
        capture_output=True, text=True, check=True)
    width, height = (int(v) for v in probe.stdout.strip().split("x"))

    raw = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"],
        capture_output=True, check=True).stdout

    expected = width * height * 3
    if len(raw) != expected:
        raise ValueError(f"{path}: expected {expected} bytes rgb24, got {len(raw)}")
    return width, height, raw


# ---------------------------------------------------------------------------------------------
# Colour. sRGB -> linear is a 256-entry table because there are only 256 possible inputs; the rest
# is the textbook D65 transform.
# ---------------------------------------------------------------------------------------------

_LINEAR = [
    (v / 255.0 / 12.92) if (v / 255.0) <= 0.04045 else (((v / 255.0 + 0.055) / 1.055) ** 2.4)
    for v in range(256)
]

_EPSILON = 216.0 / 24389.0
_KAPPA_OVER_116 = 841.0 / 108.0


def _f(t):
    return t ** (1.0 / 3.0) if t > _EPSILON else _KAPPA_OVER_116 * t + 4.0 / 29.0


def to_lab(r, g, b):
    """sRGB bytes -> CIELAB (L*, a*, b*) under D65."""
    lr, lg, lb = _LINEAR[r], _LINEAR[g], _LINEAR[b]
    fx = _f((0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb) / 0.95047)
    fy = _f(0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb)
    fz = _f((0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb) / 1.08883)
    return 116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz)


# ---------------------------------------------------------------------------------------------
# Planckian locus, via the standard Kim et al. cubic approximation of x(T) and y(T), then CIE 1931
# xy -> CIE 1960 uv. Sampled once into a table that is searched linearly; the table can be small and
# the search dumb because it runs on two mean colours, not on every pixel.
# ---------------------------------------------------------------------------------------------


def _planckian_xy(t):
    if t <= 4000.0:
        x = (-0.2661239e9 / t ** 3 - 0.2343589e6 / t ** 2 + 0.8776956e3 / t + 0.179910)
    else:
        x = (-3.0258469e9 / t ** 3 + 2.1070379e6 / t ** 2 + 0.2226347e3 / t + 0.240390)
    if t <= 2222.0:
        y = -1.1063814 * x ** 3 - 1.34811020 * x ** 2 + 2.18555832 * x - 0.20219683
    elif t <= 4000.0:
        y = -0.9549476 * x ** 3 - 1.37418593 * x ** 2 + 2.09137015 * x - 0.16748867
    else:
        y = 3.0817580 * x ** 3 - 5.87338670 * x ** 2 + 3.75112997 * x - 0.37001483
    return x, y


def _xy_to_uv(x, y):
    d = -2.0 * x + 12.0 * y + 3.0
    return 4.0 * x / d, 6.0 * y / d


_LOCUS = []
_t = 1667.0
while _t <= 25000.0:
    _LOCUS.append((_t,) + _xy_to_uv(*_planckian_xy(_t)))
    _t *= 1.0015


def duv(r, g, b):
    """(signed CIE 1960 distance from the Planckian locus, nearest CCT in K).

    Negative distance == the purple/magenta side. Zero/zero for black, which has no meaningful
    chromaticity at all — reported rather than raised so a scenario that legitimately captures a
    dark frame still gets its magnitude numbers.
    """
    lr, lg, lb = _LINEAR[r], _LINEAR[g], _LINEAR[b]
    xx = 0.4124564 * lr + 0.3575761 * lg + 0.1804375 * lb
    yy = 0.2126729 * lr + 0.7151522 * lg + 0.0721750 * lb
    zz = 0.0193339 * lr + 0.1191920 * lg + 0.9503041 * lb

    total = xx + yy + zz
    if total <= 0.0:
        return 0.0, 0.0

    u, v = _xy_to_uv(xx / total, yy / total)

    best = None
    for (t, lu, lv) in _LOCUS:
        d = math.hypot(u - lu, v - lv)
        if best is None or d < best[0]:
            best = (d, t, lv)

    d, t, lv = best
    return (d if v >= lv else -d), t


VERDICTS = [
    (1.0, "imperceptible"),
    (2.0, "visible on close inspection"),
    (5.0, "visible at a glance"),
    (float("inf"), "obvious"),
]


def verdict_for(median):
    """The standard perceptual band a median ΔE falls in."""
    return next(label for limit, label in VERDICTS if median < limit)


# ---------------------------------------------------------------------------------------------
# The measurement itself.
# ---------------------------------------------------------------------------------------------


def compare_buffers(before, after, width, height, region=None, stride=2):
    """Every statistic this module produces, from two rgb24 buffers of the same size.

    Kept separate from decode() so the arithmetic is reachable from a test with a hand-built buffer.
    Every number below is derived here and nowhere else; delta_gate.py judges but never measures.

    `stride` SUBSAMPLES — it takes every Nth pixel — rather than downscaling. Averaging neighbouring
    pixels first would smooth away exactly the per-pixel differences being measured, whereas taking
    every Nth pixel leaves the distribution (and therefore the median) intact.
    """
    if stride < 1:
        raise ValueError(f"stride must be >= 1 (got {stride})")

    expected = width * height * 3
    if len(before) != expected or len(after) != expected:
        raise ValueError(
            f"buffers do not match {width}x{height} rgb24 "
            f"(want {expected} bytes, got {len(before)} and {len(after)})")

    region = region or Region(0, 0, width, height)

    deltas = []
    sums_before = [0, 0, 0]
    sums_after = [0, 0, 0]
    # Unchanged pixels contribute exactly zero to this sum, so accumulating it only over the changed
    # ones is not an approximation — it is the same number for less work. Signed on purpose: it is
    # what a "should get brighter" assertion is actually about, and a magnitude alone cannot answer
    # that question.
    sum_delta_l = 0.0
    changed = 0
    count = 0

    for y in range(region.y, region.y + region.height, stride):
        row = y * width * 3
        for x in range(region.x, region.x + region.width, stride):
            i = row + x * 3
            br, bg, bb = before[i], before[i + 1], before[i + 2]
            ar, ag, ab = after[i], after[i + 1], after[i + 2]
            if (br, bg, bb) != (ar, ag, ab):
                l0, a0, b0 = to_lab(br, bg, bb)
                l1, a1, b1 = to_lab(ar, ag, ab)
                deltas.append(math.sqrt((l1 - l0) ** 2 + (a1 - a0) ** 2 + (b1 - b0) ** 2))
                sum_delta_l += l1 - l0
                changed += 1
            else:
                deltas.append(0.0)
            sums_before[0] += br
            sums_before[1] += bg
            sums_before[2] += bb
            sums_after[0] += ar
            sums_after[1] += ag
            sums_after[2] += ab
            count += 1

    if count == 0:
        raise ValueError("region and stride selected no pixels at all")

    deltas.sort()
    median = deltas[len(deltas) // 2]

    mean_before = tuple(round(s / count) for s in sums_before)
    mean_after = tuple(round(s / count) for s in sums_after)
    duv_before, cct_before = duv(*mean_before)
    duv_after, cct_after = duv(*mean_after)

    return {
        "FrameWidth": width,
        "FrameHeight": height,
        "Region": region.as_dict(),
        "Stride": stride,
        "SampledPixels": count,

        "MedianDeltaE": median,
        "MeanDeltaE": sum(deltas) / count,
        "P90DeltaE": deltas[int(len(deltas) * 0.90)],
        "P99DeltaE": deltas[int(len(deltas) * 0.99)],
        "ChangedFraction": changed / count,
        "Verdict": verdict_for(median),

        "MeanDeltaL": sum_delta_l / count,
        "MeanColorBefore": list(mean_before),
        "MeanColorAfter": list(mean_after),
        "DuvBefore": duv_before,
        "DuvAfter": duv_after,
        "CctBefore": cct_before,
        "CctAfter": cct_after,
    }


def compare_files(before_path, after_path, region=None, stride=2):
    """compare_buffers over two decoded image files, with the paths recorded in the result."""
    w0, h0, before = decode(before_path)
    w1, h1, after = decode(after_path)
    if (w0, h0) != (w1, h1):
        raise ValueError(f"frame sizes differ: {w0}x{h0} vs {w1}x{h1}")

    stats = compare_buffers(before, after, w0, h0, parse_region(region, w0, h0), stride)
    stats["BaselinePath"] = before_path
    stats["TargetPath"] = after_path
    return stats


# ---------------------------------------------------------------------------------------------
# CLI. Kept human-first, with --json for the gate and for anything else that wants the numbers.
# ---------------------------------------------------------------------------------------------


def format_report(stats):
    region = stats["Region"]
    lines = [
        f"frames    {stats['FrameWidth']}x{stats['FrameHeight']}, "
        f"region {region['X']},{region['Y']} {region['Width']}x{region['Height']}, "
        f"every {stats['Stride']} px ({stats['SampledPixels']} pixels)",
        f"before    {stats.get('BaselinePath', '?')}",
        f"after     {stats.get('TargetPath', '?')}",
        "",
        f"median dE {stats['MedianDeltaE']:.2f}  <- {stats['Verdict']}",
        f"mean dE   {stats['MeanDeltaE']:.2f}",
        f"p90 dE    {stats['P90DeltaE']:.2f}",
        f"p99 dE    {stats['P99DeltaE']:.2f}",
        f"changed   {stats['ChangedFraction'] * 100:.1f}% of sampled pixels",
        f"mean L*   {stats['MeanDeltaL']:+.2f} ({'brighter' if stats['MeanDeltaL'] > 0 else 'darker'})",
        "",
        f"before    rgb{tuple(stats['MeanColorBefore'])} "
        f"Duv {stats['DuvBefore']:+.5f} (nearest {stats['CctBefore']:.0f} K)",
        f"after     rgb{tuple(stats['MeanColorAfter'])} "
        f"Duv {stats['DuvAfter']:+.5f} (nearest {stats['CctAfter']:.0f} K)",
        f"hue       after sits on the "
        f"{'purple/magenta' if stats['DuvAfter'] < 0 else 'green'} side of the Planckian locus, "
        f"Duv moved {stats['DuvAfter'] - stats['DuvBefore']:+.5f}",
    ]
    return "\n".join(lines)


def build_parser():
    # argparse rather than hand-rolled flag splitting, which is how this started: `--stride 4` parsed
    # as an empty --stride plus a third positional, and the tool answered by printing its own usage
    # while silently having measured at the default stride. A measuring tool must not have a way to
    # quietly measure something other than what it was asked to.
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("baseline", help="the BEFORE frame")
    parser.add_argument("target", help="the AFTER frame")
    parser.add_argument("--region", default=FULL_REGION,
                        help=f"'{FULL_REGION}' (default) or 'X,Y,W,H' in pixels")
    parser.add_argument("--stride", type=int, default=2,
                        help="sample every Nth pixel (default 2)")
    parser.add_argument("--json", action="store_true",
                        help="emit the raw statistics instead of the human report")
    return parser


def main(argv=None):
    args = build_parser().parse_args(argv)
    stats = compare_files(args.baseline, args.target, args.region, args.stride)
    print(json.dumps(stats, indent=2) if args.json else format_report(stats))


if __name__ == "__main__":
    main()
