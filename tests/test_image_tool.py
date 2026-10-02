"""chart_image_tool: a look at the chart as the display shows it now, falling back to
the preview Unity sent. No broker, no LLM call."""
import base64
import copy
import importlib
import io
import json
from pathlib import Path
from types import SimpleNamespace

import pytest

from agent import image_tool

ASSETS = Path(__file__).resolve().parent.parent / "interaction-manager" / "Assets" / "StreamingAssets"
RAMPRICE = json.loads((ASSETS / "compiled-vl-ramprice-line-new.json").read_text())


def _graph_module():
    return importlib.import_module("agent.graph")   # the package also exports a `graph` object


@pytest.fixture
def calls(monkeypatch):
    seen = []

    def fake_create(**kwargs):
        seen.append(kwargs)
        return SimpleNamespace(output_text="The blue line is Memory.")

    monkeypatch.setattr(image_tool.client, "responses", SimpleNamespace(create=fake_create))
    return seen


def _invoke(state, question="which series is the blue line?"):
    return image_tool.chart_image_tool.invoke({"question": question, "state": state})


@pytest.fixture
def no_spec(monkeypatch):
    monkeypatch.setattr(image_tool, "get_chart_spec", lambda: None)


def test_sends_unitys_image_with_the_question(calls, no_spec):
    out = _invoke({"image_data": "QUJD", "image_format": "png"})
    assert out == "The blue line is Memory."
    content = calls[0]["input"][0]["content"]
    assert content[0]["text"] == "which series is the blue line?"
    assert content[1]["image_url"] == "data:image/png;base64,QUJD"


def test_no_image_returns_a_plain_message(calls, no_spec):
    assert "don't have an image" in _invoke({})
    assert calls == [], "no image means no vision call"


def test_vision_error_is_returned_not_raised(monkeypatch, no_spec):
    def boom(**kwargs):
        raise RuntimeError("upstream exploded")
    monkeypatch.setattr(image_tool.client, "responses", SimpleNamespace(create=boom))
    out = _invoke({"image_data": "QUJD"})
    assert "error" in out.lower() and "upstream exploded" in out


def test_tool_is_bound_to_the_data_query_loop():
    g = _graph_module()
    assert "chart_image_tool" in g._tools_by_name
    assert g.chart_image_tool in g._tools


def test_tool_schema_is_strict_compatible():
    from langchain_core.utils.function_calling import convert_to_openai_tool
    params = convert_to_openai_tool(image_tool.chart_image_tool, strict=True)["function"]["parameters"]
    assert params["additionalProperties"] is False
    assert set(params["properties"]) == {"question"}    # state is injected, never offered
    assert params["required"] == ["question"]


class TestCurrentView:
    def _line(self, n):
        return {"mark": "line", "encoding": {"x": {"field": "x"}, "y": {"field": "y"}, "color": {"field": "s"}},
                "data": {"values": [{"x": i, "y": i, "s": s} for i in range(n) for s in ("A", "B")]}}

    def test_lines_and_bars_keep_the_points_the_display_draws(self):
        view = image_tool.current_view_spec(self._line(10))
        assert sorted({r["x"] for r in view["data"]["values"]}) == list(range(7))
        scatter = dict(self._line(10), mark="point")
        assert len(image_tool.current_view_spec(scatter)["data"]["values"]) == 20   # every point

    def test_hidden_series_faint_and_colours_kept(self):
        view = image_tool.current_view_spec(RAMPRICE, hidden_series=["Memory"])
        assert view["encoding"]["color"]["scale"]["domain"] == ["GPU", "Memory", "Storage"]
        assert '["Memory"]' in view["encoding"]["opacity"]["condition"]["test"]
        assert len(view["data"]["values"]) == len(RAMPRICE["data"]["values"])   # still drawn, faint
        assert RAMPRICE["encoding"]["opacity"] == {"value": 1}                   # the loaded spec is untouched

    def test_presentation_shows_its_layer_and_pauses_the_filter(self):
        layer = image_tool.current_view_spec(RAMPRICE, hidden_series=["GPU"],
                                             presentation={"layer": "series", "series": "Storage"})
        assert '["Memory", "GPU"]' in layer["encoding"]["opacity"]["condition"]["test"]
        axis = image_tool.current_view_spec(RAMPRICE, presentation={"layer": "x_axis"})
        assert axis["encoding"]["opacity"] == {"value": image_tool.FAINT}
        title = image_tool.current_view_spec(RAMPRICE, hidden_series=["GPU"], presentation={"layer": "title"})
        assert title["encoding"]["opacity"] == {"value": 1}                       # whole chart, filter paused


def test_draws_the_current_view_and_matches_the_preview(calls, monkeypatch):
    pytest.importorskip("vl_convert")
    PIL = pytest.importorskip("PIL.Image")
    monkeypatch.setattr(image_tool, "get_chart_spec", lambda: copy.deepcopy(RAMPRICE))
    _invoke({"image_data": "QUJD", "hidden_series": []})
    url = calls[0]["input"][0]["content"][1]["image_url"]
    assert url.startswith("data:image/png;base64,") and not url.endswith("QUJD")
    drawn = PIL.open(io.BytesIO(base64.b64decode(url.split(",", 1)[1]))).convert("RGB")
    preview = PIL.open(ASSETS / "chart-line-ramprice-new.png").convert("RGB")
    assert list(drawn.getdata()) == list(preview.getdata())                     # same colours as the preview


def test_the_view_is_drawn_once_until_it_changes(monkeypatch):
    vl_convert = pytest.importorskip("vl_convert")
    drawn = []
    monkeypatch.setattr(vl_convert, "vegalite_to_png", lambda spec, scale: drawn.append(spec) or b"png")
    monkeypatch.setattr(image_tool, "_last_render", None)
    spec = copy.deepcopy(RAMPRICE)
    monkeypatch.setattr(image_tool, "get_chart_spec", lambda: spec)
    state = {"hidden_series": [], "presentation": None}
    image_tool.render_current_view(state)
    image_tool.render_current_view(dict(state))                 # same view: reused
    assert len(drawn) == 1
    image_tool.render_current_view({"hidden_series": ["GPU"], "presentation": None})
    assert len(drawn) == 2                                       # filter changed: drawn again
    spec = copy.deepcopy(RAMPRICE)                               # a new chart load
    image_tool.render_current_view({"hidden_series": ["GPU"], "presentation": None})
    assert len(drawn) == 3
