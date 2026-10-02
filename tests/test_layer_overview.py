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
        assert s["Memory"]["shape"] == "rose sharply, faster after June 2025"
        assert s["Storage"]["shape"] == "rose steadily"
        assert s["GPU"]["shape"] == "stayed flat"
        assert [s[k]["symbol"] for k in ("Memory", "Storage", "GPU")] == ["plus", "cross", "arrow up"]
        assert s["Memory"]["start"] == ["February 2025", "$190"]
        assert s["Memory"]["end"] == ["February 2026", "$1,100"]

    def test_meetings_found_with_what_follows(self):
        f = lo.compute_facts(spec("ramprice"), RAMPRICE_RENDERED)
        assert f["summary"]["crossings"] == [
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
        assert "crossings" not in f["summary"]

    def test_trend_judged_against_the_drawn_axis(self):
        # A ~$3 move is 1% of the price but fills this chart's axis: not flat, and the
        # dip at 17:00 is a real turn.
        f = lo.compute_facts(spec("aaplstock"), {"chart_type": "line"})
        assert f["series"]["data"]["shape"] == "fell to a low of $284.87 at 17:00, then rose"

    def test_a_dip_in_the_middle_is_named(self):
        f = lo.compute_facts(spec("interestrates"), {"chart_type": "line"})
        assert f["series"]["data"]["shape"] == "fell to a low of 0.13% in 2021, then rose"

    def test_percent_values_and_a_peak_only_when_in_between(self):
        f = lo.compute_facts(spec("interestrates"), {"chart_type": "line"})
        d = f["series"]["data"]
        assert d["peak"] == ["2024", "4.35%"] and "low" not in d       # the shape names the low
        f = lo.compute_facts(chart("compiled-vl-productrevenue-bar-new.json"), {"chart_type": "bar"})
        assert "peak" not in f["series"]["Software"] and "low" not in f["series"]["Software"]  # at its ends
        assert f["series"]["Hardware"]["peak"] == ["Q3", "110 thousand dollars"]

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


def chart(filename):
    """A chart from StreamingAssets with its authored text removed, as if it had none."""
    spec = json.loads((ASSETS / filename).read_text())
    spec.pop("overview", None)
    return spec


class TestLayerJobs:
    """Each layer gets only its own facts; the summary's are what only the whole chart shows."""

    def test_single_series_summary_gives_the_change_not_the_extremes_again(self):
        f = lo.compute_facts(chart("compiled-vl-waterstorage-bar-new.json"), {"chart_type": "bar"})
        assert f["series"]["data"]["shape"] == "rose to a peak of 97% in 2022, then fell"
        assert f["summary"] == {
            "change": {"direction": "higher", "by": "24 percentage points", "from": "2018", "to": "2024"},
            "biggest_rise": {"by": "15 percentage points", "from": "2020", "to": "2021"},
            "biggest_fall": {"by": "9 percentage points", "from": "2023", "to": "2024"},
        }
        summary = lo.template_layer_overview(f)["summary"]
        assert "24 percentage points higher" in summary and "97%" not in summary

    def test_stacked_bars_textures_stack_order_and_totals(self):
        rendered = {"chart_type": "bar", "series": [
            {"name": "Software", "texture": "solid"}, {"name": "Hardware", "texture": "checkerboard"},
            {"name": "Services", "texture": "vertical stripes"}]}
        f = lo.compute_facts(chart("compiled-vl-productrevenue-bar-new.json"), rendered)
        assert f["series"]["Hardware"]["opener"] == "Shown with a checkerboard texture"
        assert f["x"]["stack_order"] == ["Software", "Services", "Hardware"]
        assert f["summary"] == {
            "highest_total": ["Q4", "345 thousand dollars"], "lowest_total": ["Q1", "250 thousand dollars"],
            "totals_shape": "rose steadily", "largest_series": "Software, the largest in every bar",
        }
        assert "stacks Software, then Services, then Hardware" in lo.template_layer_overview(f)["x_axis"]
        assert f["title"] == ("Quarterly Revenue by Product Line: Software, Hardware and Services, "
                              "Q1 to Q4, stacked bar chart.")

    def test_scatterplot_groups_relationships_and_contrast(self):
        f = lo.compute_facts(chart("compiled-vl-efficiencypower-scatter-new.json"), {"chart_type": "point"})
        s = f["series"]
        assert [s[g]["relationship"].split(",")[0] for g in ("EV", "Hybrid", "ICE")] == \
            ["shows no clear pattern", "rises steadily", "rises steadily"]
        assert s["Hybrid"]["relationship"] == ("rises steadily, from Toyota Prius (90 kW, 98 g/km) "
                                               "to Honda CR-V Hybrid (235 kW, 171 g/km)")
        assert s["EV"]["sits"] == "between 57 g/km and 113 g/km, from 145 kW to 510 kW"
        assert "start" not in s["EV"]                    # no line-chart start and end
        assert f["summary"] == {
            "lowest_group": "EV", "highest_group": "ICE", "leftmost_group": "Hybrid", "rightmost_group": "EV",
            "contrast": "Hybrid and ICE rise as power increases, but the chart as a whole shows no clear pattern",
        }
        assert (f["x"]["first"], f["x"]["last"]) == ("0 kW", "600 kW")
        assert f["title"] == "Vehicle Power and CO2 Emissions: EV, Hybrid and ICE, multi-series scatterplot."

    def test_title_leaves_out_a_measure_the_name_already_says(self):
        f = lo.compute_facts(chart("compiled-vl-interestrates-line-new.json"), {"chart_type": "line"})
        assert f["title"] == "Interest Rate, 2019 to 2025, line chart."

    def test_categories_summary_gives_the_spread(self):
        bar = {"mark": "bar",
               "encoding": {"x": {"field": "device", "type": "nominal", "title": "Device"},
                            "y": {"field": "users", "type": "quantitative", "title": "Users"}},
               "data": {"values": [{"device": "Desktop", "users": 50}, {"device": "Mobile", "users": 120},
                                   {"device": "Tablet", "users": 20}]}}
        f = lo.compute_facts(bar, {"chart_type": "bar"})
        assert f["summary"] == {"difference": {"highest": "Mobile", "lowest": "Tablet", "by": "100"}}


class TestEdgeCases:
    def test_point_marks_on_dates_or_categories_still_get_text(self):
        for x_type, xs in (("nominal", ["A", "B", "C"]), ("temporal", ["2020-01-01", "2020-02-01", "2020-03-01"])):
            rows = [{"x": x, "y": v} for x, v in zip(xs, [3, 1, 2])]
            spec = _line(rows, x_type=x_type, color=False)
            spec["mark"] = "point"
            f = lo.compute_facts(spec, {"chart_type": "point"})
            d = f["series"]["data"]
            assert (d["highest"][1] if x_type == "nominal" else d["start"][1]) == "3"

    def test_duplicate_points_without_names(self):
        rows = [{"x": 1, "y": 2, "s": "A", "n": "p"}, {"x": 1, "y": 2, "s": "A"}, {"x": 3, "y": 4, "s": "A", "n": "q"}]
        spec = _line(rows, x_type="quantitative")
        spec["mark"] = "point"
        spec["encoding"]["tooltip"] = [{"field": "n"}]
        assert lo.compute_facts(spec, {"chart_type": "point"})["series"]["A"]["relationship"].endswith("to q (3, 4)")

    def test_year_on_a_scatterplot_x_has_no_thousands_separator(self):
        rows = [{"x": 2019, "y": 1}, {"x": 2020, "y": 3}, {"x": 2021, "y": 2}]
        spec = _line(rows, x_type="quantitative", color=False)
        spec["mark"] = "point"
        f = lo.compute_facts(spec, {"chart_type": "point"})
        assert (f["x"]["first"], f["x"]["last"]) == ("2019", "2021")

    def test_no_values_means_no_text(self):
        rows = [{"x": "2020-01-01", "y": None}, {"x": "2020-02-01", "y": None}]
        assert lo.compute_facts(_line(rows, color=False), {"chart_type": "line"}) is None


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
