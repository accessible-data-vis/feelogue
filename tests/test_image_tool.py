"""chart_image_tool: a look at the chart image Unity sent. No broker, no LLM call."""
import importlib
from types import SimpleNamespace

import pytest

from agent import image_tool


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


def test_sends_unitys_image_with_the_question(calls):
    out = _invoke({"image_data": "QUJD", "image_format": "png"})
    assert out == "The blue line is Memory."
    content = calls[0]["input"][0]["content"]
    assert content[0]["text"] == "which series is the blue line?"
    assert content[1]["image_url"] == "data:image/png;base64,QUJD"


def test_no_image_returns_a_plain_message(calls):
    assert "don't have an image" in _invoke({})
    assert calls == [], "no image means no vision call"


def test_vision_error_is_returned_not_raised(monkeypatch):
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
