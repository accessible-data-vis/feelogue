"""Spoken series filtering: the request is read by the model, but the series are
checked and the hidden set is computed in code."""
import pandas as pd
import pytest

import agent.filtering as flt

SERIES = ["Memory", "Storage", "GPU"]


class TestResolve:
    def test_only_hides_the_rest(self):
        hidden, msg = flt.resolve_filter("only", ["Storage"], [], SERIES)
        assert hidden == ["Memory", "GPU"] and msg == "Showing only Storage."

    def test_hide_adds_to_what_is_hidden(self):
        hidden, msg = flt.resolve_filter("hide", ["GPU"], ["Memory"], SERIES)
        assert hidden == ["Memory", "GPU"] and msg == "Showing only Storage."

    def test_show_brings_series_back(self):
        hidden, msg = flt.resolve_filter("show", ["Memory"], ["Memory", "GPU"], SERIES)
        assert hidden == ["GPU"] and msg == "Showing Memory and Storage, with GPU hidden."

    def test_show_all(self):
        hidden, msg = flt.resolve_filter("show_all", [], ["GPU"], SERIES)
        assert hidden == [] and msg == "Showing all series."

    def test_never_hides_everything(self):
        hidden, msg = flt.resolve_filter("hide", ["Storage"], ["Memory", "GPU"], SERIES)
        assert hidden is None and "every series" in msg

    def test_no_change_sends_nothing(self):
        hidden, msg = flt.resolve_filter("show_all", [], [], SERIES)
        assert hidden is None and msg == "Showing all series, as already on the chart."


def test_series_names_match_ignoring_case():
    assert flt.match_series(["storage", "GPU", "Ram"], SERIES) == (["Storage", "GPU"], ["Ram"])


class TestHandle:
    def stub(self, monkeypatch, action, series):
        monkeypatch.setattr(flt, "extract_filter_request",
                            lambda q, a, h: {"action": action, "series": series})

    def test_command_carries_the_full_hidden_set(self, monkeypatch):
        self.stub(monkeypatch, "only", ["Storage"])
        cmd, hidden, reply = flt.handle_filter_request("Filter everything apart from Storage", SERIES, [])
        assert cmd == {"filter": {"hidden_series": ["Memory", "GPU"]}}
        assert hidden == ["Memory", "GPU"] and reply == "Showing only Storage."

    def test_unknown_series_named_back_with_the_real_ones(self, monkeypatch):
        self.stub(monkeypatch, "hide", ["RAM"])
        cmd, hidden, reply = flt.handle_filter_request("hide RAM", SERIES, [])
        assert cmd is None and hidden == []
        assert reply == "I couldn't find RAM on this chart. Its series are Memory, Storage and GPU."

    def test_single_series_chart(self, monkeypatch):
        cmd, hidden, reply = flt.handle_filter_request("hide it", ["data"], [])
        assert cmd is None and "single series" in reply


def test_filter_node_uses_the_charts_series(monkeypatch):
    import importlib
    g = importlib.import_module("agent.graph")   # the package also exports a `graph` object
    df = pd.DataFrame({"month": ["Feb", "Feb", "Feb"], "price": [190, 330, 850],
                       "series": SERIES, "in_view": [True, True, True]})
    monkeypatch.setattr(g, "get_df", lambda: df)
    monkeypatch.setattr(flt, "extract_filter_request", lambda q, a, h: {"action": "only", "series": ["Storage"]})
    out = g.filter_node({"current_query": "only Storage", "current_intent": "filter",
                         "color_field": "series", "hidden_series": []})
    assert out["rtd_command"] == {"filter": {"hidden_series": ["Memory", "GPU"]}}
    assert out["hidden_series"] == ["Memory", "GPU"]
    assert out["intent_responses"] == {"filter": "Showing only Storage."}


def test_single_shot_intents_are_not_run_twice(monkeypatch):
    import json
    import agent.intent as it

    class Resp:
        choices = [type("C", (), {"message": type("M", (), {"content": json.dumps({"intents": [
            {"type": "filter", "query": "put GPU back"}, {"type": "filter", "query": "on"},
            {"type": "data_analysis", "query": "average"}, {"type": "data_analysis", "query": "max"}],
            "has_deictic": False})})()})()]
    monkeypatch.setattr(it.client.chat.completions, "create", lambda **kw: Resp())
    types = [i["type"] for i in it.classify_query("put GPU back on")["intents"]]
    assert types == ["filter", "data_analysis", "data_analysis"]


# The filter during the presentation, and what questions cover

def test_unchanged_filter_is_still_sent_during_the_presentation():
    hidden, msg = flt.resolve_filter("show_all", [], [], SERIES, force_send=True)
    assert hidden == [] and msg == "Showing all series."


def test_filter_node_ends_the_presentation_even_when_nothing_changes(monkeypatch):
    import importlib
    g = importlib.import_module("agent.graph")
    df = pd.DataFrame({"month": ["Feb"] * 3, "price": [190, 330, 850], "series": SERIES})
    monkeypatch.setattr(g, "get_df", lambda: df)
    monkeypatch.setattr(flt, "extract_filter_request", lambda q, a, h: {"action": "show", "series": ["Memory"]})
    out = g.filter_node({"current_query": "show Memory", "current_intent": "filter", "color_field": "series",
                         "hidden_series": [], "presentation": {"layer": "series", "series": "Storage"}})
    assert out["rtd_command"] == {"filter": {"hidden_series": []}}
    assert out["intent_responses"] == {"filter": "Showing all series."}


def _frame(visible=(True, True, True)):
    return pd.DataFrame({"month": ["Feb"] * 3, "price": [190, 330, 850], "series": SERIES,
                         "in_view": list(visible)})


def test_filtered_column_comes_from_the_agents_own_filter(monkeypatch):
    import agent.context as ctx
    monkeypatch.setattr(ctx, "_df", _frame(visible=(True, False, True)))
    state = {"color_field": "series", "hidden_series": ["Storage"]}
    f = ctx.frame_for_questions(state)
    assert list(f["hidden_by_filter"]) == [False, True, False]
    assert ctx.frame_for_questions(state) is f              # same object: the pandas executor stays cached


def test_filter_is_paused_while_the_presentation_runs(monkeypatch):
    import agent.context as ctx
    base = _frame()
    monkeypatch.setattr(ctx, "_df", base)
    state = {"color_field": "series", "hidden_series": ["Storage"], "presentation": {"layer": "title"}}
    assert "hidden_by_filter" not in ctx.frame_for_questions(state).columns


class TestScopeRules:
    """Which scope applies. The checks look for the names and the kind of rule,
    not exact wording, so rewording a prompt doesn't break them."""

    def rules(self, **kw):
        from agent.prompts.data_query import get_data_query_scope
        return get_data_query_scope(kw.get("df"), kw.get("hidden"), kw.get("presentation"))

    def test_series_layer_narrows_to_that_series(self):
        text = self.rules(presentation={"layer": "series", "series": "Storage"})
        assert "Storage" in text and "whole chart" not in text

    def test_other_layers_mean_the_whole_chart(self):
        for layer in ("title", "x_axis", "y_axis", "summary"):
            assert "whole chart" in self.rules(presentation={"layer": layer})

    def test_presentation_pauses_the_filter(self):
        assert "GPU" not in self.rules(hidden=["GPU"], presentation={"layer": "title"})

    def test_hidden_series_are_named(self):
        assert "GPU" in self.rules(hidden=["GPU"])

    def test_points_beyond_the_display_add_a_rule(self):
        df = _frame(visible=(True, True, False))
        assert self.rules(df=df) != ""
        df["hidden_by_filter"] = [False, False, True]          # the only unseen row is a filtered one
        assert self.rules(df=df, hidden=["GPU"]) == self.rules(hidden=["GPU"])

    def test_nothing_applies_nothing_added(self):
        assert self.rules(df=_frame()) == ""


def test_prefix_describes_only_the_columns_present():
    from agent.prompts.data_query import get_data_query_prefix
    df = _frame(visible=(True, False, True))
    assert "in_view" in get_data_query_prefix("series", list(df.columns), df)
    assert "hidden_by_filter" not in get_data_query_prefix("series", list(df.columns), df)
    df["hidden_by_filter"] = [False, True, False]
    assert "hidden_by_filter" in get_data_query_prefix("series", list(df.columns), df)
    assert "in_view" not in get_data_query_prefix("series", ["series"], _frame().drop(columns="in_view"))
