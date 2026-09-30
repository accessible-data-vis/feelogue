"""Pointing-word routing, the row id kept out of prompt text, the JSON parser,
follow-up chunking, and the data-query loop (last round, stable prompt prefix)."""
import importlib

import agent.touch_context as tc
from agent.utils import parse_llm_json

g = importlib.import_module("agent.graph")   # the package also exports a `graph` object

TOUCH = {"left_touch": {"touch_timestamp": 1, "nodes": {
    "data-mark-3": {"probability": 1.0, "node_values": {"year": 2021, "rate": 0.13, "_id": "row-2"}}}}}


def route(monkeypatch, query, intent="chart_overview"):
    monkeypatch.setattr(g, "classify_query", lambda q, messages=None: {
        "intents": [{"type": intent, "query": q}], "has_deictic": True})
    return g.classifier_node({"user_query": query, "messages": [], "touchdata": TOUCH,
                              "highlighted_context": {}})["intents"][0]["type"]


def test_this_chart_is_the_chart_not_the_touched_point(monkeypatch):
    assert route(monkeypatch, "describe this chart") == "chart_overview"
    assert route(monkeypatch, "walk me through that graph") == "chart_overview"


def test_pointing_words_still_go_to_the_touched_point(monkeypatch):
    assert route(monkeypatch, "what is this") == "data_analysis"
    assert route(monkeypatch, "tell me about it") == "data_analysis"


def test_row_id_stays_out_of_the_touch_text_but_in_the_node():
    info, nodes = tc.collect_touch_nodes(TOUCH)
    text = info[0][1]
    assert "row-2" not in text and "_id" not in text and "0.13" in text
    assert nodes["data-mark-3"]["node_values"]["_id"] == "row-2"


def test_parser_only_accepts_an_answer_object():
    fallback = {"message": "fallback"}
    assert parse_llm_json("4.35", fallback) is fallback
    assert parse_llm_json("[1, 2]", fallback) is fallback
    assert parse_llm_json('{"message": "ok"}', fallback) == {"message": "ok"}


def test_a_follow_up_question_is_one_chunk():
    question = "I found a few possible charts: rainfall line chart, rainfall bar chart. Which would you like?"
    out = g.post_process_node({"intent_responses": {"load_chart": question}, "followup_stage": True,
                               "user_query": "open rainfall", "intents": []})
    assert out["chunks"] == [question]


def test_an_answer_is_still_split_into_sentences():
    out = g.post_process_node({"intent_responses": {"data_analysis": "It rose. Then it fell."},
                               "followup_stage": False, "user_query": "q", "intents": []})
    assert out["chunks"] == ["It rose.", "Then it fell."]


class _Resp:
    def __init__(self, tool_calls, content=""):
        self.tool_calls, self.content = tool_calls, content


class _LLM:
    def __init__(self, resp):
        self.resp, self.calls = resp, 0

    def invoke(self, msgs):
        self.calls += 1
        return self.resp


class _Tool:
    def invoke(self, args):
        return "4.35"


def test_the_last_round_answers_without_tools(monkeypatch):
    monkeypatch.setattr(g, "_tools_by_name", {"csv_query_tool": _Tool()})
    looping = _LLM(_Resp([{"name": "csv_query_tool", "args": {"query": "q"}, "id": "t1"}]))
    final = _LLM(_Resp([], '{"message": "It was 4.35 percent.", "highlighted_ids": []}'))
    raw = g._run_tool_loop({"messages": []}, "what was the rate", max_iterations=3,
                           llm=looping, final_llm=final)
    assert looping.calls == 2 and final.calls == 1
    assert raw == '{"message": "It was 4.35 percent.", "highlighted_ids": []}'


class _Recorder:
    """Records each request; calls a tool on the first round, then answers."""
    def __init__(self, tool_rounds=0):
        self.requests, self.tool_rounds = [], tool_rounds

    def invoke(self, msgs):
        self.requests.append(list(msgs))
        if len(self.requests) <= self.tool_rounds:
            return _Resp([{"name": "csv_query_tool", "args": {"query": "q"}, "id": f"t{len(self.requests)}"}])
        return _Resp([], '{"message": "ok", "highlighted_ids": []}')


def _run(monkeypatch, state, tool_rounds=0):
    import pandas as pd
    import agent.context as ctx
    monkeypatch.setattr(ctx, "_df", pd.DataFrame({"month": ["Jan", "Jan"], "price": [1, 2],
                                                   "series": ["A", "B"], "in_view": [True, True]}))
    monkeypatch.setattr(g, "_tools_by_name", {"csv_query_tool": _Tool()})
    llm = _Recorder(tool_rounds)
    g._run_tool_loop({"messages": [], "color_field": "series", **state}, "what is the average",
                     max_iterations=4, llm=llm, final_llm=llm)
    return llm.requests


def test_system_prompt_is_the_same_whatever_the_scope(monkeypatch):
    plain = _run(monkeypatch, {})[0]
    scoped = _run(monkeypatch, {"hidden_series": ["B"], "presentation": {"layer": "series", "series": "A"}})[0]
    assert plain[0].content == scoped[0].content                 # cached prefix unchanged
    assert "shows only A" in scoped[-2].content                  # scope rides after the history
    assert "shows only A" not in scoped[0].content


def test_each_round_extends_the_previous_request(monkeypatch):
    first, second = _run(monkeypatch, {}, tool_rounds=1)
    assert [m.content for m in second[:len(first)]] == [m.content for m in first]
    assert "Iterations remaining" in second[-1].content          # budget on the last message
