#!/usr/bin/env python3
"""Runner/compose_ab.py — pair two frame sequences into one labelled A/B video.

WHY THIS EXISTS
---------------
A still can only *assert* that an effect is a narrow transient; it cannot show it, and a reviewer is
right to suspect a still was picked from a sweep. run_test.sh already films the sweep (Timelapse /
TickLapse) and stitches each sequence into its own video, but two separate videos are not evidence
anyone can check at a glance — they have to be scrubbed in lockstep, which nobody does. This pairs
them frame-for-frame into a single side-by-side, burns in a per-frame caption so the reader can see
*where* in the sweep they are, and marks the frames whose off/on pair actually differs.

That last mark is the point, and it is MEASURED from the rendered PNGs rather than asserted. It is
the cheap boolean half of the question frame_delta.py answers expensively: a run that captures 79
frames and finds 50 byte-identical and 29 differing in one contiguous block has demonstrated the
window, not described it. The banner is what makes that visible without reading a number.

WHY IT KNOWS NOTHING ABOUT WHAT IT IS FILMING
---------------------------------------------
Captions are supplied, never derived. This started life inside a mod that burned in the sun
elevation it had fitted from its own scenario's probe expectations, which was useful there and
meaningless anywhere else — a tool in the harness that knows what a "sun elevation" is has a mod
baked into it. So the caption is either a linear ramp the caller describes (`--caption-from` /
`--caption-step`, which is exactly what a Timelapse's `fromHour`/`stepHours` or a TickLapse's
`ticks` already are) or a file of per-frame lines the caller generated (`--captions-file`, which is
how a mod burns in anything it derives itself).

Depends on nothing but ffmpeg and the standard library, like everything else under Runner/.

    python3 compose_ab.py REPORTS_DIR OFF_PREFIX ON_PREFIX OUT_BASENAME [options]

Writes OUT_BASENAME.mp4 (full size, for the archive) and OUT_BASENAME.gif (downscaled — GitHub
renders a GIF inline from a raw URL but will not give a player to a video from one).
"""

import argparse
import filecmp
import os
import shutil
import subprocess
import tempfile

FONT_CANDIDATES = [
    "/usr/share/fonts/TTF/DejaVuSans.ttf",
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
    "/usr/share/fonts/noto/NotoSans-Regular.ttf",
]


def find_font():
    for path in FONT_CANDIDATES:
        if os.path.exists(path):
            return path
    raise SystemExit("no usable TTF found; add one to FONT_CANDIDATES")


def escape_drawtext(text):
    """Make one caption safe to embed in a drawtext filter.

    Two layers bite here, and getting either wrong produces a caption that silently truncates rather
    than an error anyone notices. Inside a filtergraph, ':' separates a filter's options and '\\'
    escapes; inside drawtext, a quoted value ends at the next unescaped "'". Order matters —
    backslashes first, or the escapes we add get escaped in turn.

    '%' is left alone because the callers set expansion=none (see build_filter): disabling drawtext's
    own %{...} expansion is more robust than escaping every caption that mentions a percentage.
    """
    return text.replace("\\", "\\\\").replace("'", "\\'").replace(":", "\\:")


def frame_paths(reports, prefix):
    """Every frame of one sequence, in the order the expander numbered them.

    Mirrors Shared/TimelapseExpander's naming ({prefix}_{index:04d}.png) and stops at the first gap,
    which is also what run_test.sh's glob does.
    """
    index = 0
    out = []
    while True:
        path = os.path.join(reports, f"{prefix}_{index:04d}.png")
        if not os.path.exists(path):
            return out
        out.append(path)
        index += 1


def frames_differ(a, b):
    """Whether two rendered frames differ at all.

    A byte comparison of the encoded PNGs, which is sound here only because the harness writes them
    through one deterministic encoder in one run: identical pixels give identical files. It is the
    cheap half of the question frame_delta.py answers expensively, and a boolean is all this caller
    needs.
    """
    return not filecmp.cmp(a, b, shallow=False)


def caption_for(index, args, from_file):
    """The per-frame caption line: a file's Nth line, or the format applied to a linear ramp."""
    if from_file is not None:
        return from_file[index] if index < len(from_file) else ""
    value = args.caption_from + index * args.caption_step
    return args.caption.format(index=index, value=value)


def load_captions(path):
    if path is None:
        return None
    with open(path, encoding="utf-8") as handle:
        return [line.rstrip("\n") for line in handle]


def build_filter(font, left_label, right_label, info, status, status_colour):
    """One filtergraph turning the off frame and the on frame into a labelled 1280x412 pair."""
    def text(body, x, y, size, colour):
        return (f"drawtext=fontfile={font}:text='{escape_drawtext(body)}':x={x}:y={y}"
                f":fontsize={size}:fontcolor={colour}:expansion=none")

    return (
        "[0:v]scale=640:360[l];[1:v]scale=640:360[r];[l][r]hstack=inputs=2[s];"
        "[s]pad=1280:412:0:52:black[p];"
        "[p]drawbox=x=639:y=52:w=2:h=360:color=white@0.30:t=fill[b];[b]"
        + text(left_label, 16, 8, 22, "white")
        + "," + text(right_label, 656, 8, 22, "white")
        + "," + text(info, 16, 34, 15, "0xB0B0B0")
        + "," + text(status, 656, 34, 15, status_colour)
    )


def compose(args):
    font = find_font()
    captions = load_captions(args.captions_file)

    off = frame_paths(args.reports, args.off_prefix)
    on = frame_paths(args.reports, args.on_prefix)
    if not off or not on:
        raise SystemExit(
            f"no frames found for '{args.off_prefix}' / '{args.on_prefix}' in {args.reports}")
    if len(off) != len(on):
        raise SystemExit(
            f"sequences differ in length ({len(off)} vs {len(on)}) — not a frame-for-frame pair")

    # The first screenshot of a run can still carry UI chrome that hidden-UI mode has not finished
    # tearing down, which makes frame 0 of the two sweeps differ for a reason that has nothing to do
    # with the subsystem under test. Dropping it is honest; leaving it in would put a false
    # "frames differ" mark on a frame outside the window.
    first = 1 if args.skip_first else 0

    work = tempfile.mkdtemp(prefix="compose_ab-")
    try:
        for out_index, i in enumerate(range(first, len(off))):
            differs = frames_differ(off[i], on[i])
            status = "frames differ" if differs else "frames byte-identical"
            colour = "0xE8A0FF" if differs else "0x8A8A8A"

            subprocess.run(
                ["ffmpeg", "-v", "error", "-y", "-i", off[i], "-i", on[i],
                 "-filter_complex", build_filter(
                     font, args.left_label, args.right_label,
                     caption_for(i, args, captions), status, colour),
                 os.path.join(work, f"ab_{out_index:04d}.png")],
                check=True)

        pattern = os.path.join(work, "ab_%04d.png")

        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-framerate", str(args.fps), "-i", pattern,
             "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "18",
             "-movflags", "+faststart", args.out + ".mp4"],
            check=True)

        # Downscaled and palette-reduced on purpose. A GIF is the only thing GitHub will animate
        # inline from a raw URL, and an inline image that takes a visible beat to load is one a
        # reviewer scrolls past. stats_mode=diff spends the palette on the pixels that actually move
        # across the sweep, which is where all the useful colour is.
        subprocess.run(
            ["ffmpeg", "-v", "error", "-y", "-framerate", str(args.fps), "-i", pattern,
             "-vf", f"scale={args.gif_width}:-2:flags=lanczos,split[a][b];"
                    f"[a]palettegen=max_colors={args.gif_colors}:stats_mode=diff[p];"
                    f"[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
             "-loop", "0", args.out + ".gif"],
            check=True)
    finally:
        shutil.rmtree(work, ignore_errors=True)

    print(f"wrote {args.out}.mp4 and {args.out}.gif "
          f"({len(off) - first} frames at {args.fps} fps)")


def main():
    p = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("reports", help="directory holding the two frame sequences")
    p.add_argument("off_prefix", help="fileNamePrefix of the feature-off sequence")
    p.add_argument("on_prefix", help="fileNamePrefix of the feature-on sequence")
    p.add_argument("out", help="output basename; .mp4 and .gif are appended")
    p.add_argument("--caption", default="frame {index}",
                   help="format applied per frame; {index} and {value} are available")
    p.add_argument("--caption-from", type=float, default=0.0,
                   help="{value} at frame 0 (e.g. a Timelapse's fromHour)")
    p.add_argument("--caption-step", type=float, default=1.0,
                   help="how much {value} advances per frame (e.g. a Timelapse's stepHours)")
    p.add_argument("--captions-file",
                   help="one caption per line, overriding --caption entirely; for anything the "
                        "caller derives itself rather than ramps linearly")
    p.add_argument("--fps", type=int, default=10)
    p.add_argument("--left-label", default="OFF")
    p.add_argument("--right-label", default="ON")
    p.add_argument("--gif-width", type=int, default=800)
    p.add_argument("--gif-colors", type=int, default=96)
    p.add_argument("--skip-first", action="store_true",
                   help="drop frame 0 of each sweep (un-settled UI chrome)")
    compose(p.parse_args())


if __name__ == "__main__":
    main()
