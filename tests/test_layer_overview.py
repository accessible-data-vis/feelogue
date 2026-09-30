"""Generated presentation text: facts from the chart data, the model's answer
checked, the fixed-sentence fallback, and what is sent to Unity."""
import json
from pathlib import Path

import pytest

import agent.layer_overview as lo

ASSETS = Path(__file__).resolve().parent.parent / "interaction-manager" / "Assets" / "StreamingAssets"


def spec(name):
    return json.loads((ASSETS / f"compiled-vl-{name}-line-new.json").read_text())


RAMPRICE_RENDERED = {
    "chart_type": "line",
    "series": [{"name": "Memory", "symbol": "plus"}, {"name": "Storage", "symbol": "cross"},
               {"name": "GPU", "symbol": "arrow up"}],
    "y_domain": [0, 1200], "y_ticks": [0, 200, 400, 600, 800, 1000, 1200],
    "points_shown": 7, "points_total": 7,
}


class TestComputeFacts:
    def test_multi_series_line_shapes_and_symbols(self):
        f = lo.compute_facts(spec("ramprice"), RAMPRICE_RENDERED)
        assert f["layer_keys"] == ["title", "x_axis", "y_axis", "Memory", "Storage", "GPU", "summary"]
        s = f["series"]
        assert s["Memory"]["shape"] == "rose sharply, speeding up from June 2025"
        assert s["Storage"]["shape"] == "rose steadily"
        assert s["GPU"]["shape"] == "stayed flat"
        assert [s[k]["symbol"] for k in ("Memory", "Storage", "GPU")] == ["plus", "cross", "arrow up"]
        assert s["Memory"]["start"] == ["February 2025", "$190"]
        assert s["Memory"]["end"] == ["February 2026", "$1,100"]

    def test_meetings_found_with_what_follows(self):
        f = lo.compute_facts(spec("ramprice"), RAMPRICE_RENDERED)
        assert f["crossings"] == [
            {"series": ["Memory", "Storage"], "at": "August 2025", "value": "$360", "kind": "meet",
             "afterwards": "Memory pulls away from Storage"},
            {"series": ["Memory", "GPU"], "at": "December 2025", "value": "$850", "kind": "meet",
             "afterwards": "Memory pulls away from GPU"},
        ]

    def test_axes_and_title_parts(self):
        f = lo.compute_facts(spec("ramprice"), RAMPRICE_RENDERED)
        assert f["y"]["range"] == ["$0", "$1,200"] and f["y"]["tick_step"] == "$200"
        assert f["title"] == ("Computer Component Prices: average price of Memory, Storage and GPU, "
                              "February 2025 to February 2026, multi-series line chart.")
        assert "shown_at_once" not in f["x"]          # all 7 points fit

    def test_single_series_uses_data_key_and_axis_format(self):
        rendered = {"chart_type": "line", "series": [{"name": "data", "symbol": "plus"}]}
        f = lo.compute_facts(spec("aaplstock"), rendered)
        assert f["layer_keys"] == ["title", "x_axis", "y_axis", "data", "summary"]
        assert f["x"]["first"] == "16:00"          # the axis format, not the ISO timestamp
        assert f["series"]["data"]["start"][1].startswith("$")
        assert f["series"]["data"]["symbol"] == "plus"
        assert f["title"] == "AAPL Intraday, May 6, 2026: close price, 16:00 to 22:00, line chart."
        assert f["crossings"] == []

    def test_trend_judged_against_the_drawn_axis(self):
        # A ~$3 move is 1% of the price but fills this chart's axis: not flat.
        f = lo.compute_facts(spec("aaplstock"), {"chart_type": "line"})
        assert f["series"]["data"]["shape"] == "rose unevenly"

    def test_percent_values(self):
        f = lo.compute_facts(spec("interestrates"), {"chart_type": "line"})
        assert f["series"]["data"]["highest"] == ["2024", "4.35%"]

    def test_bar_chart_of_categories_has_extremes_not_trends(self):
        bar = {"mark": "bar",
               "encoding": {"x": {"field": "device", "type": "nominal", "title": "Device"},
                            "y": {"field": "users", "type": "quantitative", "title": "Users"}},
               "data": {"values": [{"device": "Desktop", "users": 50},
                                   {"device": "Mobile", "users": 120},
                                   {"device": "Tablet", "users": 20}]}}
        f = lo.compute_facts(bar, {"chart_type": "bar", "series": None})
        d = f["series"]["data"]
        assert "shape" not in d and d["symbol"] is None
        assert d["highest"] == ["Mobile", "120"] and d["lowest"] == ["Tablet", "20"]
        assert f["title"] == "Users, Desktop to Tablet, bar chart."

    def test_no_inline_data(self):
        assert lo.compute_facts({"encoding": {"x": {"field": "a"}, "y": {"field": "b"}}}, None) is None


class _FakeResponse:
    def __init__(self, content):
        self.choices = [type("C", (), {"message": type("M", (), {"content": content})()})()]


class TestGenerate:
    FACTS = {"title": "T.", "layer_keys": ["title", "x_axis", "y_axis", "data", "summary"]}

    def fake(self, monkeypatch, answer):
        monkeypatch.setattr(lo.client.chat.completions, "create",
                            lambda **kw: _FakeResponse(json.dumps(answer)))

    def test_complete_answer_kept_in_layer_order(self, monkeypatch):
        self.fake(monkeypatch, {"summary": "S.", "x_axis": "X.", "y_axis": "Y.", "data": "D."})
        assert list(lo.generate_layer_overview(self.FACTS)) == self.FACTS["layer_keys"]

    def test_missing_layer_rejected(self, monkeypatch):
        self.fake(monkeypatch, {"x_axis": "X.", "y_axis": "Y.", "summary": "S."})
        assert lo.generate_layer_overview(self.FACTS) is None


def test_generated_text_is_written_once_and_sent_on_every_load(monkeypatch):
    import agent.mqtt_handler as mh
    sent, calls = [], []

    class Client:
        def publish(self, topic, payload, qos=0, retain=False):
            sent.append((topic, json.loads(payload)))

    monkeypatch.setattr(mh, "_mqtt_client", Client())
    monkeypatch.setattr(mh, "generate_layer_overview",
                        lambda facts: calls.append(facts) or {k: "text" for k in facts["layer_keys"]})
    schema = spec("aaplstock")
    rendered = {"chart_type": "line", "series": [{"name": "data", "symbol": "plus"}]}
    mh._publish_generated_overview(schema, rendered, "aaplstock-test")
    mh._publish_generated_overview(schema, rendered, "aaplstock-test")
    assert len(calls) == 1                      # the second load reuses the cached text
    assert len(sent) == 2
    topic, body = sent[0]
    assert body["chart_overview_for_rtd"]["data_name"] == "aaplstock-test"
    assert list(body["chart_overview_for_rtd"]["overview"]) == ["title", "x_axis", "y_axis", "data", "summary"]


def _line(rows, x_type="temporal", color=True):
    enc = {"x": {"field": "x", "type": x_type, "title": "X"},
           "y": {"field": "y", "type": "quantitative", "title": "Y"}}
    if color:
        enc["color"] = {"field": "s"}
    return {"mark": "line", "encoding": enc, "data": {"values": rows}}


class TestFixes:
    def test_numeric_series_names_are_text(self):
        rows = [{"x": "2020-01-01", "y": 1, "s": 2019}, {"x": "2020-02-01", "y": 2, "s": 2019},
                {"x": "2020-01-01", "y": 3, "s": 2020}, {"x": "2020-02-01", "y": 1, "s": 2020}]
        f = lo.compute_facts(_line(rows), {"chart_type": "line", "series": [{"name": "2019", "symbol": "plus"}]})
        assert f["layer_keys"] == ["title", "x_axis", "y_axis", "2019", "2020", "summary"]
        assert f["series"]["2019"]["symbol"] == "plus"
        assert "of 2019 and 2020" in f["title"]

    def test_one_big_step_does_not_crash(self):
        rows = [{"x": "2020-01-01", "y": 0}, {"x": "2021-01-01", "y": 100}]
        f = lo.compute_facts(_line(rows, color=False), {"chart_type": "line"})
        assert f["series"]["data"]["shape"] == "rose sharply"

    def test_scatter_x_runs_smallest_to_largest(self):
        rows = [{"x": 34, "y": 1}, {"x": 5, "y": 2}, {"x": 12, "y": 3}]
        spec = _line(rows, x_type="quantitative", color=False)
        spec["mark"] = "point"
        f = lo.compute_facts(spec, {"chart_type": "point"})
        assert (f["x"]["first"], f["x"]["last"]) == ("5", "34")

    def test_unsorted_dates_read_in_time_order(self):
        rows = [{"x": "2021-01-01", "y": 3}, {"x": "2019-01-01", "y": 1}, {"x": "2020-01-01", "y": 2}]
        f = lo.compute_facts(_line(rows, color=False), {"chart_type": "line"})
        assert (f["x"]["first"], f["x"]["last"]) == ("2019-01-01", "2021-01-01")
        assert f["series"]["data"]["shape"].startswith("rose")   # file order would read "fell"

    def test_values_spoken_as_the_data_has_them(self):
        rows = [{"x": "2020-01-01", "y": 0.125}, {"x": "2020-02-01", "y": 1234.5}]
        f = lo.compute_facts(_line(rows, color=False), {"chart_type": "line", "y_ticks": [0, 0.1, 0.2]})
        assert f["series"]["data"]["start"][1] == "0.125"      # not rounded to 0.13
        assert f["series"]["data"]["end"][1] == "1,234.5"
        assert f["y"]["tick_step"] == "0.1"                    # float noise cleared

    def test_fixed_sentences_cover_every_layer(self):
        f = lo.compute_facts(spec("ramprice"), RAMPRICE_RENDERED)
        text = lo.template_layer_overview(f)
        assert list(text) == f["layer_keys"]
        assert all(isinstance(v, str) and v.strip() for v in text.values())
        assert "shown with the plus symbol" in text["Memory"]
        assert "Memory and Storage meet at August 2025" in text["summary"]


class _Client:
    def __init__(self):
        self.sent = []

    def publish(self, topic, payload, qos=0, retain=False):
        self.sent.append(json.loads(payload)["chart_overview_for_rtd"])


def test_failed_phrasing_still_answers_unity_with_fixed_sentences(monkeypatch):
    import agent.mqtt_handler as mh
    client = _Client()
    monkeypatch.setattr(mh, "_mqtt_client", client)
    monkeypatch.setattr(mh, "generate_layer_overview", lambda facts: None)
    mh._publish_generated_overview(spec("ramprice"), RAMPRICE_RENDERED, "ramprice-fallback", "line")
    body = client.sent[-1]
    assert body["chart_type"] == "line"
    assert list(body["overview"]) == ["title", "x_axis", "y_axis", "Memory", "Storage", "GPU", "summary"]
    assert mh.get_generated_overview("ramprice-fallback", "x") is None     # fixed sentences aren't cached


def test_nothing_to_describe_answers_with_no_overview(monkeypatch):
    import agent.mqtt_handler as mh
    client = _Client()
    monkeypatch.setattr(mh, "_mqtt_client", client)
    mh._publish_generated_overview({"encoding": {}}, None, "empty-chart", "line")
    assert client.sent == [{"data_name": "empty-chart", "chart_type": "line", "overview": None}]
