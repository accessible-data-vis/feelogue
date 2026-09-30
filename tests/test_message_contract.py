"""The agent's side of docs/messages.md: what it sends matches the documented
fields, and it reads the documented Unity messages."""
import json
import re
from pathlib import Path

import pytest

import agent.mqtt_handler as mh

DOC = (Path(__file__).resolve().parent.parent / "docs" / "messages.md").read_text()


def documented_fields(message: str) -> tuple[set[str], set[str]]:
    """(required, optional) field names from the message's table in the doc."""
    section = DOC.split(f"### `{message}`", 1)[1].split("\n### ", 1)[0].split("\n## ", 1)[0]
    required, optional = set(), set()
    for row in re.findall(r"^\| (`[^|]+`) \| (.*) \|$", section, re.M):
        names = re.findall(r"`([^`]+)`", row[0])
        (optional if "*optional*" in row[1] else required).update(names)
    return required, optional


class _Client:
    def __init__(self):
        self.sent = []

    def publish(self, topic, payload, qos=0, retain=False):
        self.sent.append(json.loads(payload))


@pytest.fixture
def client(monkeypatch):
    c = _Client()
    monkeypatch.setattr(mh, "_mqtt_client", c)
    return c


def test_reply_sends_the_documented_fields(client):
    required, optional = documented_fields("agent_response_for_user")
    mh.publish_message("Hi.")
    minimal = set(client.sent[-1]["agent_response_for_user"])
    mh.publish_message("Hi.", rtd_command={"filter": {"hidden_series": []}}, nodes={"node_1": {"id": "row-0"}},
                       referents={"touch_used": False}, presentation="start")
    full = set(client.sent[-1]["agent_response_for_user"])
    assert minimal == required
    assert full == required | optional


def test_presentation_text_sends_the_documented_fields(client, monkeypatch):
    required, optional = documented_fields("chart_overview_for_rtd")
    mh._publish_generated_overview({"encoding": {}}, None, "empty-chart", "line")
    assert set(client.sent[-1]["chart_overview_for_rtd"]) == required | optional


def test_agent_reads_a_chart_load(monkeypatch):
    patches = []
    monkeypatch.setattr(mh, "reset_context_keep_messages", lambda: None)
    monkeypatch.setattr(mh.graph, "update_state", lambda cfg, patch: patches.append(patch))
    msg = {"rtd_data_for_agent": {"chart_type": "line", "data_name": "airfares",
                                  "schema": {"encoding": {"color": {"field": "route"}}, "overview": {"title": "T"}},
                                  "rendered": {"chart_type": "line"}}}
    mh.on_message(None, None, type("M", (), {"payload": json.dumps(msg).encode()}))
    assert patches[-1]["data_name"] == "airfares" and patches[-1]["color_field"] == "route"
    assert patches[-1]["chart_overview"] == {"title": "T"}


def test_agent_reads_layer_data(monkeypatch):
    import agent.context as ctx
    monkeypatch.setattr(ctx, "_df", None)
    monkeypatch.setattr(mh.graph, "update_state", lambda cfg, patch: None)
    msg = {"message_type": "layer_data_update", "layer_name": "airfares", "chart_type": "line",
           "x_field": "month", "y_field": "fare", "series_field": "route", "data_count": 1,
           "data": [{"month": "2020-01-01", "fare": 210, "route": "SYD-MEL", "_id": "row-0", "in_view": True}]}
    mh.on_message(None, None, type("M", (), {"payload": json.dumps(msg).encode()}))
    assert {"_id", "in_view", "route"} <= set(ctx.get_df().columns)


def test_agent_reads_a_question(monkeypatch):
    import agent.orchestrator as orch
    seen = {}
    monkeypatch.setattr(orch.graph, "invoke", lambda patch, cfg: seen.update(patch) or {"final_response": "ok"})
    msg = {"user_request_for_agent": {
        "transcript": {"text_transcript": "what is this", "confidence": 0.9, "words": []},
        "touchdata": {"left_touch": "No touch", "right_touch": "No touch"},
        "highlighted_context": "No highlight",
        "presentation": {"layer": "series", "series": "SYD-MEL"}}}
    orch.process_user_request(json.dumps(msg))
    assert seen["user_query"] == "what is this"
    assert seen["presentation"] == {"layer": "series", "series": "SYD-MEL"}
