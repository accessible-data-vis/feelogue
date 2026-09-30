"""A request that loads a chart does only the load; the rest runs on the new chart's
data, survives a "which chart?" question, and is dropped with a message otherwise."""
import importlib
import json
import time

import pytest

import agent.context as ctx

g = importlib.import_module("agent.graph")   # the package also exports a `graph` object

FILTER = {"type": "filter", "query": "hide Sydney"}
LOAD = {"type": "load_chart", "query": "open airfares"}
OVERVIEW = {"type": "chart_overview", "query": "walk me through it"}


@pytest.fixture(autouse=True)
def clean_hold():
    ctx.take_held()
    yield
    ctx.take_held()


def classify(monkeypatch, intents):
    monkeypatch.setattr(g, "classify_query", lambda q, messages=None: {"intents": [dict(i) for i in intents],
                                                                        "has_deictic": False})
    return g.classifier_node({"user_query": "q", "messages": [], "touchdata": {}, "highlighted_context": {}})


def load(monkeypatch, rtd, followup=False, held_intents=()):
    monkeypatch.setattr(g, "analyze_user_intent_with_context",
                        lambda q, s: {"response": "Loading airfares." if rtd else "Which one?",
                                      "rtd_command": rtd, "followup_stage": followup})
    return g.load_chart_node({"current_query": "open airfares", "current_intent": "load_chart",
                              "held_intents": list(held_intents)})


def test_the_load_runs_first_and_the_rest_is_held(monkeypatch):
    out = classify(monkeypatch, [FILTER, LOAD])
    assert out["intents"] == [LOAD] and out["held_intents"] == [FILTER]


def test_a_load_that_goes_out_holds_the_rest_and_skips_the_presentation(monkeypatch):
    out = load(monkeypatch, "airfares-line", held_intents=[FILTER])
    assert ctx.held_stage() == "awaiting_load" and ctx.held_pieces() == [FILTER]
    assert out["presentation_command"] == "skip" and out["held_note"] is None


def test_an_overview_alone_is_not_held(monkeypatch):
    out = load(monkeypatch, "airfares-line", held_intents=[OVERVIEW])
    assert ctx.held_stage() is None and out["presentation_command"] is None


def test_the_rest_survives_a_which_chart_question(monkeypatch):
    load(monkeypatch, None, followup=True, held_intents=[FILTER])
    assert ctx.held_stage() == "awaiting_choice"
    out = load(monkeypatch, "airfares-line")            # the answer to "which one?"
    assert ctx.held_stage() == "awaiting_load" and ctx.held_pieces() == [FILTER]
    assert out["presentation_command"] == "skip"


def test_a_load_that_fails_says_what_did_not_run(monkeypatch):
    out = load(monkeypatch, None, held_intents=[FILTER])
    assert ctx.held_stage() is None
    assert out["held_note"] == "I didn't do the rest of your request, hide Sydney, since no chart was loaded."


def test_moving_on_drops_the_rest_with_a_note(monkeypatch):
    ctx.hold_pieces([FILTER], "awaiting_choice")
    out = classify(monkeypatch, [{"type": "data_analysis", "query": "what is the average"}])
    assert ctx.held_stage() is None and "hide Sydney" in out["held_note"]


def test_held_pieces_run_as_a_turn_without_classifying():
    out = g.classifier_node({"user_query": "hide Sydney", "preset_intents": [FILTER]})
    assert out["intents"] == [FILTER] and out["current_intent"] == "filter" and out["preset_intents"] is None


class _Msg:
    def __init__(self, data):
        self.payload = json.dumps(data).encode()


def test_new_chart_data_runs_the_held_rest(monkeypatch):
    import agent.mqtt_handler as mh
    published, ran = [], []
    monkeypatch.setattr(mh, "reset_context_keep_messages", lambda: None)
    monkeypatch.setattr(mh.graph, "update_state", lambda cfg, patch: None)
    monkeypatch.setattr(mh, "update_dataframe_from_layer", lambda data: None)
    monkeypatch.setattr(mh, "process_held_request", lambda pieces: ran.append(pieces) or {"response": "Hid Sydney."})
    monkeypatch.setattr(mh, "publish_message", lambda **kw: published.append(kw))

    ctx.hold_pieces([FILTER], "awaiting_load")
    mh.on_message(None, None, _Msg({"message_type": "layer_data_update", "data": []}))
    assert ran == []                                   # data from before the load: not yet
    mh.on_message(None, None, _Msg({"rtd_data_for_agent": {"data_name": "airfares", "chart_type": "line",
                                                           "schema": {"overview": {"title": "T"}}}}))
    assert ctx.held_stage() == "awaiting_data"
    mh.on_message(None, None, _Msg({"message_type": "layer_data_update", "data": []}))
    assert ran == [[FILTER]] and published[-1]["response_text"] == "Hid Sydney."
    assert ctx.held_stage() is None


def test_a_chart_that_never_loads_expires_with_a_message(monkeypatch):
    import agent.mqtt_handler as mh
    published = []
    monkeypatch.setattr(mh, "publish_message", lambda **kw: published.append(kw))
    monkeypatch.setattr(mh, "HELD_EXPIRY_S", 0.01)
    gen = ctx.hold_pieces([FILTER], "awaiting_load")
    mh._expire_held_later(gen)
    time.sleep(0.2)
    assert ctx.held_stage() is None
    assert published == [{"response_text": "I didn't do the rest of your request, hide Sydney, since the chart didn't load."}]


def test_expiry_leaves_a_newer_hold_alone(monkeypatch):
    import agent.mqtt_handler as mh
    monkeypatch.setattr(mh, "publish_message", lambda **kw: None)
    monkeypatch.setattr(mh, "HELD_EXPIRY_S", 0.01)
    old = ctx.hold_pieces([FILTER], "awaiting_load")
    ctx.hold_pieces([FILTER], "awaiting_load")          # a later request held its own pieces
    mh._expire_held_later(old)
    time.sleep(0.2)
    assert ctx.held_stage() == "awaiting_load"
