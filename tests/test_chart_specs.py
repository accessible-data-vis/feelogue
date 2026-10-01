"""Every chart spec carries what Unity needs to load and present it."""
import json
from pathlib import Path

import pytest

ASSETS = Path(__file__).resolve().parent.parent / "interaction-manager" / "Assets" / "StreamingAssets"
CHARTS = sorted(ASSETS.glob("compiled-vl-*.json"))   # discovery scans the root only, like Unity


def series_of(spec: dict) -> list[str]:
    field = (spec["encoding"].get("color") or {}).get("field")
    if not field:
        return []
    return list(dict.fromkeys(str(row[field]) for row in spec["data"]["values"] if field in row))


@pytest.mark.parametrize("chart", CHARTS, ids=lambda p: p.name)
def test_spec_has_what_unity_needs(chart):
    spec = json.loads(chart.read_text())
    meta = spec.get("metadata") or {}
    # ChartDiscoveryService skips a spec without these.
    assert meta.get("dataName") and meta.get("chartType") and meta.get("displayName")

    series = series_of(spec)
    overview = spec.get("overview")
    if overview:
        # Each series is a presentation layer, looked up by its exact name.
        assert not set(series) - set(overview), f"no layer text for {set(series) - set(overview)}"

    shape = spec["encoding"].get("shape")
    if shape and series:
        # A series left out takes the Inspector's symbol, which can match another's.
        assert not set(series) - set(shape["scale"]["domain"])

    fields = {k for row in spec["data"]["values"] for k in row}
    tooltip = spec["encoding"].get("tooltip") or []
    for d in tooltip if isinstance(tooltip, list) else [tooltip]:
        assert d["field"] in fields, f"the tooltip names {d['field']}, which no row has"
