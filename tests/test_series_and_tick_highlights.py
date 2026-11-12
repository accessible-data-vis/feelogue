"""Whole-series and axis-tick highlights: resolving them to nodes, placing a series
as one item, and the drawn ticks reaching the agent and its prompt."""
import importlib
import json
from types import SimpleNamespace

import pandas as pd

import agent.context as ctx
import agent.postprocessing as pp
from agent.postprocessing import (assign_nodes_to_chunks, resolve_highlighted_series,
                                  resolve_tick_nodes, series_point_ids)
from agent.prompts.data_query import get_data_query_system_prompt

g = importlib.import_module("agent.graph")   # the package also exports a `graph` object


def frame(**extra):
    df = pd.DataFrame({
        "DATE": ["2019", "2020", "2019", "2020", "2021"],
        "PRICE": [10, 12, 20, 25, 30],
        "CITY": ["Perth", "Perth", "Hobart", "Hobart", "Hobart"],
        "_id": ["p0", "p1", "h0", "h1", "h2"],
    })
    return df.assign(**extra)


def _resp(content):
    return SimpleNamespace(choices=[SimpleNamespace(message=SimpleNamespace(content=content))])


# ── series ───────────────────────────────────────────────────────────────────

def test_a_series_expands_to_its_points():
    assert series_point_ids(frame(), ["Hobart"], "CITY") == ["h0", "h1", "h2"]
    assert series_point_ids(frame(), ["Darwin"], "CITY") == []
    assert series_point_ids(frame(), ["Hobart"], None) == []


def test_hidden_and_off_display_points_are_left_out():
    df = frame(hidden_by_filter=[True, True, False, False, False],
               in_view=[True, True, True, True, False])
    assert series_point_ids(df, ["Perth"], "CITY") == []
    assert series_point_ids(df, ["Hobart"], "CITY") == ["h0", "h1"]


def test_series_nodes_are_grouped_and_keep_the_row_id():
    nodes = resolve_highlighted_series(["Perth"], frame(), "DATE", "PRICE", "CITY")
    assert set(nodes) == {"series:Perth/p0", "series:Perth/p1"}
    assert nodes["series:Perth/p0"] == {"id": "p0", "x": "2019", "y": 10, "CITY": "Perth",
                                        "group": "series:Perth"}


def test_a_series_is_placed_as_one_item(monkeypatch):
    nodes = resolve_highlighted_series(["Perth"], frame(), "DATE", "PRICE", "CITY")
    nodes["h2"] = {"id": "h2", "x": "2021", "y": 30}
    sent = []

    def fake_create(**kw):
        sent.append(kw["messages"][0]["content"])
        return _resp(json.dumps({"assignments": [{"id": "series:Perth", "chunk": 0},
                                                 {"id": "h2", "chunk": 1}]}))

    monkeypatch.setattr(pp.client.chat.completions, "create", fake_create)
    out = assign_nodes_to_chunks(nodes, ["Perth rises.", "Hobart peaks in 2021."])
    assert out["series:Perth/p0"]["chunk"] == 0 and out["series:Perth/p1"]["chunk"] == 0
    assert out["h2"]["chunk"] == 1
    assert "series:Perth/p0" not in sent[0] and "series:Perth" in sent[0]


# ── ticks ────────────────────────────────────────────────────────────────────

def test_tick_ids_resolve_to_drawn_ticks():
    nodes = resolve_tick_nodes(["x-axis-tick-1", "y-axis-tick-0", "x-axis-tick-9", "p0"],
                               ["2019", "2020"], [0, 50])
    assert nodes == {"x-axis-tick-1": {"id": "x-axis-tick-1", "value": "2020"},
                     "y-axis-tick-0": {"id": "y-axis-tick-0", "value": 0}}
    assert resolve_tick_nodes(["x-axis-tick-0"], None, None) == {}


def test_prompt_lists_the_ticks_by_id():
    prompt = get_data_query_system_prompt({}, x_field="DATE", y_field="PRICE",
                                          x_ticks=["2019", "2020"], y_ticks=[0, 50])
    assert "DATE ticks, left to right: x-axis-tick-0 = 2019, x-axis-tick-1 = 2020" in prompt
    assert "PRICE ticks, bottom to top: y-axis-tick-0 = 0, y-axis-tick-1 = 50" in prompt
    assert "**Axis ticks**" not in get_data_query_system_prompt({}, x_field="DATE", y_field="PRICE")


def test_layer_update_keeps_the_last_ticks_of_a_hidden_axis(monkeypatch):
    patches = []
    monkeypatch.setattr(g.graph, "update_state", lambda config, patch: patches.append(patch))
    msg = {"layer_name": "line", "x_field": "DATE", "y_field": "PRICE",
           "data": [{"DATE": "2019", "PRICE": 10}], "x_ticks": ["2019"], "y_ticks": []}
    patch = ctx.update_dataframe_from_layer(msg)
    assert patch["x_ticks"] == ["2019"]
    assert "y_ticks" not in patch


# ── data_query_node ──────────────────────────────────────────────────────────

def test_data_query_node_resolves_ids_ticks_and_series(monkeypatch):
    answer = json.dumps({"message": "Perth rises from 2019 to 2020.",
                         "highlighted_ids": ["h2", "x-axis-tick-0"],
                         "highlighted_series": ["Perth"]})
    monkeypatch.setattr(g, "_run_tool_loop", lambda **kw: answer)
    monkeypatch.setattr(g, "get_df", lambda: frame())
    monkeypatch.setattr(ctx, "_df", frame())
    monkeypatch.setattr(g, "rewrite_long_node_lists_with_gpt", lambda t: t)
    out = g.data_query_node({"current_query": "how does Perth change?", "current_intent": "trend",
                             "x_field": "DATE", "y_field": "PRICE", "color_field": "CITY",
                             "x_ticks": ["2019", "2020", "2021"]})
    assert set(out["nodes_by_id"]) == {"h2", "x-axis-tick-0", "series:Perth/p0", "series:Perth/p1"}


def test_a_hidden_series_is_not_highlighted(monkeypatch):
    answer = json.dumps({"message": "Perth is hidden.", "highlighted_ids": [],
                         "highlighted_series": ["Perth"]})
    monkeypatch.setattr(g, "_run_tool_loop", lambda **kw: answer)
    monkeypatch.setattr(g, "get_df", lambda: frame())
    monkeypatch.setattr(ctx, "_df", frame())
    monkeypatch.setattr(g, "rewrite_long_node_lists_with_gpt", lambda t: t)
    out = g.data_query_node({"current_query": "q", "current_intent": "trend",
                             "x_field": "DATE", "y_field": "PRICE", "color_field": "CITY",
                             "hidden_series": ["Perth"]})
    assert out["nodes_by_id"] == {}
