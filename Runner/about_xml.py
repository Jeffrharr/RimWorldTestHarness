#!/usr/bin/env python3
"""Read a mod folder's OWN packageId out of its About/About.xml.

WHY THIS IS NOT A ONE-LINE REGEX, which is what it used to be in two places in run_test.sh:

    re.search(r"<packageId>(.*?)</packageId>", about)

That takes the FIRST packageId element in the file, and About.xml has more than one kind. A mod's
`<modDependencies>` block names each dependency by packageId, and nothing says a mod must declare its
own id before its dependencies -- ModMetaData is a class, not an ordered schema, and RimWorld does not
care. Clouds (brrainz.clouds, workshop 3039192325) puts `<modDependencies>` first, so the old regex
read its Harmony dependency's id and confidently reported the mod as `brrainz.harmony`.

The consequence was the worst shape a harness bug can take. `--mod <clouds>` symlinked the folder into
Mods/ and then wrote `brrainz.harmony` into the run's ModsConfig -- an id that was already there -- so
Clouds was installed, never activated, and never loaded. The run went green through every step, took
its screenshots, and reported probes measuring a game the mod under test was absent from. Nothing in
the log said "not activated"; the modlist simply listed Harmony twice over.

So this reads the ROOT element's direct packageId child, which is the only one that is the mod's own,
and it is a module rather than two copies of a snippet so the two callers cannot drift apart again.
"""

import re
import sys
import xml.etree.ElementTree as ET

# Dependency blocks, i.e. every element that legitimately contains OTHER mods' packageIds. Only used
# by the regex fallback below; the parsed path gets this right structurally.
_DEPENDENCY_BLOCKS = re.compile(
    r"<(modDependencies|modDependenciesByVersion|incompatibleWith|loadAfter|loadBefore)\b.*?</\1>",
    re.S | re.I,
)


def package_id(mod_dir):
    """The lowercased packageId a mod declares for itself. Exits non-zero if there is none."""
    path = f"{mod_dir}/About/About.xml"
    # utf-8-sig, not utf-8: About.xml is frequently written by Windows tooling and carries a BOM,
    # which ET reports as "XML or text declaration not at start of entity" rather than as an encoding
    # problem.
    with open(path, encoding="utf-8-sig") as f:
        about = f.read()

    found = _from_tree(about) or _from_text(about)
    if not found:
        sys.exit(f"no <packageId> of its own in {path}")
    return found.strip().lower()


def _from_tree(about):
    """The real answer: the root ModMetaData element's own direct child."""
    try:
        root = ET.fromstring(about)
    except ET.ParseError:
        # Hand-edited About.xml files are not reliably well-formed -- a stray unescaped ampersand in
        # a description is enough -- and refusing to run over a malformed DESCRIPTION would be a
        # harsher tool than the one this replaces. Fall through to the text scan.
        return None

    node = root.find("packageId")
    return None if node is None else (node.text or "")


def _from_text(about):
    """Fallback for a file ET will not parse: same scan as before, minus the dependency blocks."""
    stripped = _DEPENDENCY_BLOCKS.sub("", about)
    m = re.search(r"<packageId>(.*?)</packageId>", stripped, re.S | re.I)
    return None if m is None else m.group(1)


if __name__ == "__main__":
    print(package_id(sys.argv[1]))
