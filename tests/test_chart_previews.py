"""Every preview image must be drawn from its chart's current spec."""
import importlib.util
import json
import sys
from pathlib import Path

import pytest

ASSETS = Path(__file__).resolve().parent.parent / "interaction-manager" / "Assets" / "StreamingAssets"
sys.dont_write_bytecode = True   # no __pycache__ inside StreamingAssets
_spec = importlib.util.spec_from_file_location("chart_preview_stamp", ASSETS / "Tools" / "chart_preview_stamp.py")
stamp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(stamp)

CHARTS = sorted(ASSETS.glob("compiled-vl-*.json"))   # discovery scans the root only, like Unity


def preview_for(spec: dict) -> Path | None:
    """The preview Unity loads (ChartDiscoveryService.ResolvePreviewImage)."""
    meta = spec.get("usermeta") or {}
    if meta.get("previewImage"):
        return ASSETS / meta["previewImage"]
    variant = f"-{meta['variant']}" if meta.get("variant") else ""
    for name in (f"chart-{meta.get('chartType')}-{meta.get('dataName')}{variant}-new.png",
                 f"chart-{meta.get('chartType')}-{meta.get('dataName')}-new.png"):
        if (ASSETS / name).exists():
            return ASSETS / name
    return None


def test_there_are_charts_to_check():
    assert CHARTS


@pytest.mark.parametrize("chart", CHARTS, ids=lambda p: p.name)
def test_preview_matches_its_spec(chart):
    spec = json.loads(chart.read_text())
    png = preview_for(spec)
    assert png is not None, f"{chart.name} has no preview image"
    redraw = (f"redraw it: python interaction-manager/Assets/StreamingAssets/Tools/generate_chart_preview.py "
              f"--json-path {chart.relative_to(ASSETS.parent.parent.parent)} --png-path {png.relative_to(ASSETS.parent.parent.parent)}")
    found = stamp.read_stamp(png.read_bytes())
    assert found is not None, f"{png.name} was not drawn by the preview tool; {redraw}"
    assert found == stamp.spec_digest(spec), f"{png.name} is older than {chart.name}; {redraw}"


def test_editing_presentation_text_needs_no_redraw():
    spec = {"mark": "line", "overview": {"title": "A"}, "usermeta": {"dataName": "x"}}
    edited = {**spec, "overview": {"title": "B"}, "description": "new"}
    assert stamp.spec_digest(spec) == stamp.spec_digest(edited)
    assert stamp.spec_digest(spec) != stamp.spec_digest({**spec, "mark": "bar"})


def test_stamp_round_trip_keeps_the_image_valid():
    import struct, zlib
    ihdr = b"IHDR" + struct.pack(">IIBBBBB", 1, 1, 8, 0, 0, 0, 0)
    def chunk(body):
        return struct.pack(">I", len(body) - 4) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(ihdr) + chunk(b"IDAT" + zlib.compress(b"\x00\x00")) + chunk(b"IEND")
    once = stamp.stamp_png(png, "abc")
    twice = stamp.stamp_png(once, "def")                  # restamping replaces, never stacks
    assert stamp.read_stamp(once) == "abc" and stamp.read_stamp(twice) == "def"
    assert twice.count(stamp.STAMP_KEY) == 1
