#!/usr/bin/env python3
"""Offline tests for Runner/about_xml.py — "which packageId in About.xml is the mod's OWN".

The bug these exist to keep dead is not subtle once you see it, and was invisible until someone ran a
mod that happened to declare its dependencies first. `--mod` used to take the first packageId element
in About.xml, and `<modDependencies>` is full of other mods' ids, so passing Clouds (brrainz.clouds,
which lists its Harmony dependency above its own id) put `brrainz.harmony` into the run's ModsConfig.
The folder got symlinked into Mods/ and then never activated. Every step passed, screenshots were
taken, probes were read — of a game that had never loaded the mod under test.

That is the failure class the runner's whole design is meant to refuse: a green run that verified
nothing. So the cases below are mostly about ORDER and about the blocks that legitimately contain
other mods' ids, not about parsing XML.

Run: python3 -m unittest discover -s Tests/runner   (or via ./test.sh)
"""

import importlib.util
import os
import tempfile
import unittest

RUNNER_DIR = os.path.join(
    os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "Runner"
)

_spec = importlib.util.spec_from_file_location("about_xml", os.path.join(RUNNER_DIR, "about_xml.py"))
about_xml = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(about_xml)


def mod_dir(tmp, about, bom=False):
    """Write an About.xml into a temp mod folder and return the folder."""
    d = os.path.join(tmp, "Mod")
    os.makedirs(os.path.join(d, "About"), exist_ok=True)
    with open(os.path.join(d, "About", "About.xml"), "w", encoding="utf-8-sig" if bom else "utf-8") as f:
        f.write(about)
    return d


class ReadsTheModsOwnId(unittest.TestCase):
    def read(self, about, bom=False):
        with tempfile.TemporaryDirectory() as tmp:
            return about_xml.package_id(mod_dir(tmp, about, bom))

    def test_plain_about_reads_its_packageid(self):
        self.assertEqual(
            self.read("<ModMetaData><packageId>joof.example</packageId></ModMetaData>"),
            "joof.example",
        )

    # THE REGRESSION. Clouds' shape exactly: dependencies declared above the mod's own id.
    def test_dependency_declared_first_does_not_win(self):
        about = """<?xml version="1.0" encoding="utf-8"?>
<ModMetaData>
  <name>Clouds</name>
  <modDependencies>
    <li>
      <packageId>brrainz.harmony</packageId>
      <displayName>Harmony</displayName>
    </li>
  </modDependencies>
  <packageId>brrainz.clouds</packageId>
</ModMetaData>"""
        self.assertEqual(self.read(about), "brrainz.clouds")

    # loadAfter/incompatibleWith name other mods too. They use <li> rather than <packageId> today, so
    # the old regex survived them by luck; pinned so a schema that grows packageId elements there
    # cannot resurrect the bug.
    def test_load_order_blocks_do_not_win(self):
        about = """<ModMetaData>
  <loadAfter><li>ludeon.rimworld</li><packageId>some.framework</packageId></loadAfter>
  <incompatibleWith><packageId>other.mod</packageId></incompatibleWith>
  <packageId>joof.example</packageId>
</ModMetaData>"""
        self.assertEqual(self.read(about), "joof.example")

    # RimWorld matches packageIds case-insensitively and writes them lowercased into ModsConfig; the
    # runner has always emitted lowercase, and every comparison downstream assumes it.
    def test_id_is_lowercased(self):
        self.assertEqual(
            self.read("<ModMetaData><packageId>Joof.MixedCase</packageId></ModMetaData>"),
            "joof.mixedcase",
        )

    def test_surrounding_whitespace_is_stripped(self):
        self.assertEqual(
            self.read("<ModMetaData><packageId>\n  joof.example\n</packageId></ModMetaData>"),
            "joof.example",
        )

    # Workshop About.xml files are routinely written by Windows tooling and carry a BOM. Decoded as
    # plain utf-8 that reads as a stray character before the declaration and ElementTree refuses the
    # whole file, which would silently drop every such mod onto the fallback path.
    def test_byte_order_mark_is_tolerated(self):
        self.assertEqual(
            self.read("<ModMetaData><packageId>joof.example</packageId></ModMetaData>", bom=True),
            "joof.example",
        )


class FallsBackWithoutSilentlyGuessing(unittest.TestCase):
    def read(self, about):
        with tempfile.TemporaryDirectory() as tmp:
            return about_xml.package_id(mod_dir(tmp, about))

    # A hand-edited About.xml with an unescaped ampersand in its description is common enough that
    # refusing to run over one would be a harsher tool than the regex it replaces. The text fallback
    # still has to skip the dependency block.
    def test_malformed_xml_still_reads_the_right_id(self):
        about = """<ModMetaData>
  <description>Rock & roll</description>
  <modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>
  <packageId>joof.example</packageId>
</ModMetaData>"""
        self.assertEqual(self.read(about), "joof.example")

    # A mod with ONLY dependency ids has no id of its own, and the runner must say so rather than
    # activate someone else's mod in its place.
    def test_no_own_id_exits_rather_than_taking_a_dependencys(self):
        about = """<ModMetaData>
  <modDependencies><li><packageId>brrainz.harmony</packageId></li></modDependencies>
</ModMetaData>"""
        with self.assertRaises(SystemExit):
            self.read(about)


if __name__ == "__main__":
    unittest.main()
