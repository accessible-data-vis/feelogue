"""Loading a chart by name, and the overview request."""
import importlib
import json
from types import SimpleNamespace

import agent.chart_loader as cl

g = importlib.import_module("agent.graph")   # the package also exports a `graph` object

CHARTS = {"charts": [
    {"chart_id": 0, "data_name": "airfares", "chart_type": "line", "chart_name": "Domestic airfares"},
    {"chart_id": 1, "data_name": "rainfall", "chart_type": "line", "chart_name": "Rainfall line chart"},
    {"chart_id": 2, "data_name": "rainfall", "chart_type": "bar", "chart_name": "Rainfall bar chart"},
]}


def resolve(monkeypatch, matches, catalogue=CHARTS):
    reply = SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=json.dumps({"matches": matches})))])
    monkeypatch.setattr(cl.client.chat.completions, "create", lambda **kw: reply)
    return cl.analyze_user_intent_with_context("open the chart", {"chart_metadata_index": catalogue, "messages": []})


def test_one_match_loads_it_in_unitys_format(monkeypatch):
    out = resolve(monkeypatch, [{"chart_id": 0, "chart_name": "Domestic airfares"}])
    assert out["rtd_command"] == "airfares-line"          # ButtonGUI splits on the last hyphen
    assert out["followup_stage"] is False


def test_several_matches_ask_which(monkeypatch):
    out = resolve(monkeypatch, [{"chart_id": 1, "chart_name": "Rainfall line chart"},
                                {"chart_id": 2, "chart_name": "Rainfall bar chart"}])
    assert out["rtd_command"] is None and out["followup_stage"] is True
    assert [c["chart_id"] for c in out["pending_chart_options"]] == [1, 2]
    assert "Rainfall line chart" in out["response"] and "Rainfall bar chart" in out["response"]


def test_no_match_offers_charts_and_waits_for_an_answer(monkeypatch):
    out = resolve(monkeypatch, [])
    assert out["rtd_command"] is None and out["followup_stage"] is True
    assert "Domestic airfares" in out["response"]


def test_a_match_missing_from_the_catalogue_loads_nothing(monkeypatch):
    out = resolve(monkeypatch, [{"chart_id": 9, "chart_name": "Ghost"}])
    assert out["rtd_command"] is None and out["followup_stage"] is False


def test_no_catalogue_means_no_model_call(monkeypatch):
    monkeypatch.setattr(cl.client.chat.completions, "create", lambda **kw: (_ for _ in ()).throw(AssertionError))
    out = cl.analyze_user_intent_with_context("open airfares", {"chart_metadata_index": {}})
    assert out["rtd_command"] is None


def test_overview_request_starts_the_presentation():
    out = g.chart_overview_node({"current_intent": "chart_overview", "chart_type": "line", "df_columns": ["month"]})
    assert out["presentation_command"] == "start"


def test_overview_without_a_chart_says_so():
    out = g.chart_overview_node({"current_intent": "chart_overview"})
    assert "presentation_command" not in out
    assert "load a chart" in out["intent_responses"]["chart_overview"]
