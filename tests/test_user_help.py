"""user_help: the <current_chart> block, the chart facts the node reads (spec titles,
display series, hidden series), and that the node sends them in the user message
while the manual-bearing system prompt stays fixed.

No broker, no LLM call: the chat completion is stubbed.
"""
import importlib
from types import SimpleNamespace

import pandas as pd

import agent.context as ctx
from agent.prompts.user_help import USER_HELP_SYSTEM_PROMPT, get_user_help_prompt

g = importlib.import_module("agent.graph")   # the package also exports a `graph` object

SPEC = {"mark": "line", "encoding": {
    "x": {"field": "DATE", "type": "temporal", "title": "Quarter"},
    "y": {"field": "PRICE", "type": "quantitative", "title": "Median House Price (AUD)"},
    "color": {"field": "CITY", "type": "nominal"}}}
MARKS = [{"name": "Brisbane", "symbol": "circle"}, {"name": "Melbourne", "symbol": "square"},
         {"name": "Sydney", "symbol": "triangle"}]


# ── prompt block ─────────────────────────────────────────────────────────────

def test_manual_is_in_the_system_prompt():
    assert "<user_manual>" in USER_HELP_SYSTEM_PROMPT
    assert "The user manual is not available." not in USER_HELP_SYSTEM_PROMPT


def test_prompt_without_chart_says_not_loaded():
    prompt = get_user_help_prompt("how do I hide a line?", chart=None)
    assert "<current_chart>\nChart loaded: no\n</current_chart>" in prompt


def test_prompt_lists_chart_facts():
    prompt = get_user_help_prompt("how do I hide a line?", chart={
        "chart_type": "line chart", "x_title": "Month", "y_title": "Interest rate (%)",
        "series": ["Cash rate", "Inflation"], "hidden_series": [],
    })
    for line in ("Chart loaded: yes", "Chart type: line chart", "X axis: Month",
                 "Y axis: Interest rate (%)", "Series: Cash rate, Inflation", "Hidden series: none"):
        assert line in prompt


def test_prompt_leaves_out_missing_facts():
    prompt = get_user_help_prompt("q", chart={
        "chart_type": "line chart", "x_title": None, "y_title": None,
        "series": [], "hidden_series": [],
    })
    assert "Chart type: line chart" in prompt
    assert "X axis" not in prompt and "Y axis" not in prompt
    assert "Series" not in prompt


# ── node ─────────────────────────────────────────────────────────────────────

def _run_user_help(monkeypatch, state, spec=None):
    sent = []

    def fake_create(**kwargs):
        sent.append(kwargs)
        return SimpleNamespace(choices=[SimpleNamespace(
            message=SimpleNamespace(content="Say **Hide Sydney** to hide that line."))])

    monkeypatch.setattr(ctx, "_chart_spec", spec)
    monkeypatch.setattr(g.client.chat.completions, "create", fake_create)
    patch = g.user_help_node({"current_intent": "user_help",
                              "current_query": "how do I hide a line?",
                              "messages": [], **state})
    return patch, sent[0]["messages"]


def test_node_sends_the_live_chart_in_the_user_message(monkeypatch):
    patch, messages = _run_user_help(monkeypatch, {
        "chart_type": "line", "data_name": "property", "color_field": "CITY",
        "display_marks": MARKS, "hidden_series": ["Sydney"]}, SPEC)
    assert messages[0] == {"role": "system", "content": USER_HELP_SYSTEM_PROMPT}
    user = messages[1]["content"]
    assert "Chart type: line chart" in user
    assert "Series: Brisbane, Melbourne, Sydney" in user
    assert "Hidden series: Sydney" in user
    assert "X axis: Quarter" in user and "Y axis: Median House Price (AUD)" in user
    assert "CITY" not in user and "PRICE" not in user and "DATE" not in user
    assert patch["intent_responses"]["user_help"] == "Say Hide Sydney to hide that line."


def test_node_without_chart(monkeypatch):
    _, messages = _run_user_help(monkeypatch, {})
    assert "Chart loaded: no" in messages[1]["content"]


def test_single_series_chart_lists_no_series(monkeypatch):
    spec = {"mark": "bar", "encoding": {"x": {"field": "Q", "title": "Quarter"},
                                        "y": {"field": "R", "title": "Revenue"}}}
    _, messages = _run_user_help(monkeypatch, {
        "chart_type": "bar", "data_name": "revenue", "color_field": None,
        "display_marks": [{"name": "R", "texture": "solid"}]}, spec)
    user = messages[1]["content"]
    assert "Chart type: bar chart" in user
    assert "Series" not in user


def test_series_fall_back_to_the_data(monkeypatch):
    monkeypatch.setattr(ctx, "_df", pd.DataFrame({"CITY": ["Perth", "Hobart", "Perth"], "V": [1, 2, 3]}))
    _, messages = _run_user_help(monkeypatch, {
        "chart_type": "bar", "data_name": "x", "color_field": "CITY", "display_marks": None}, SPEC)
    user = messages[1]["content"]
    assert "Chart type: stacked bar chart" in user
    assert "Series: Perth, Hobart" in user


def test_a_failed_call_still_answers(monkeypatch):
    def boom(**kwargs):
        raise RuntimeError("model unavailable")
    monkeypatch.setattr(g.client.chat.completions, "create", boom)
    patch = g.user_help_node({"current_intent": "user_help", "current_query": "q", "messages": []})
    assert patch["intent_responses"]["user_help"] == "Sorry, I couldn't answer that help question right now."


# ── routing and reply ────────────────────────────────────────────────────────

def test_help_routes_to_its_node():
    assert g.route_intent({"current_intent": "user_help"}) == "user_help_node"


def test_help_answer_is_split_without_highlights():
    out = g.post_process_node({"intent_responses": {"user_help": "Hold function key 1. Then speak."},
                               "nodes_by_id": {"r1": {"id": "r1"}},
                               "followup_stage": False, "user_query": "q", "intents": []})
    assert out["chunks"] == ["Hold function key 1.", "Then speak."]
    assert out["nodes"] == {}


def test_empty_help_answer_has_no_chunks():
    out = g.post_process_node({"intent_responses": {"user_help": "  "},
                               "followup_stage": False, "user_query": "q", "intents": []})
    assert out["chunks"] == []
