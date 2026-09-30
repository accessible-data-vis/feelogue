"""split_into_chunks, and highlights named by the answer and placed on its sentences."""
import json
from types import SimpleNamespace

import pandas as pd
import pytest

import agent.postprocessing as pp
from agent.postprocessing import split_into_chunks, resolve_highlighted_nodes, assign_nodes_to_chunks


# =============================================================================
# split_into_chunks
# =============================================================================

class TestSplitIntoChunks:
    def test_single_sentence(self):
        assert split_into_chunks("The maximum point is Sydney.") == [
            "The maximum point is Sydney."
        ]

    def test_two_sentences_with_thousands_separators(self):
        text = ("The maximum point is Sydney on 1 March 2025, where the median "
                "house price, CLOSE, is 1,512,625 Australian dollars. "
                "The lowest for Sydney was 1st March 2019, at 913,000.")
        assert split_into_chunks(text) == [
            "The maximum point is Sydney on 1 March 2025, where the median "
            "house price, CLOSE, is 1,512,625 Australian dollars.",
            "The lowest for Sydney was 1st March 2019, at 913,000.",
        ]

    def test_decimal_is_not_a_boundary(self):
        assert split_into_chunks("The rate was 4.35 percent. Then it fell.") == [
            "The rate was 4.35 percent.",
            "Then it fell.",
        ]

    def test_sentence_ending_in_a_number(self):
        assert split_into_chunks("The price peaked at 913,000. After that it fell.") == [
            "The price peaked at 913,000.",
            "After that it fell.",
        ]

    def test_numbered_list_items_keep_their_numbers(self):
        text = "Here are the points: 1. Sydney rose sharply. 2. Brisbane stayed flat."
        assert split_into_chunks(text) == [
            "Here are the points:",
            "1. Sydney rose sharply.",
            "2. Brisbane stayed flat.",
        ]

    def test_empty_and_whitespace(self):
        assert split_into_chunks("") == []
        assert split_into_chunks("   ") == []
        assert split_into_chunks(None) == []


# =============================================================================
# resolve_highlighted_nodes
# =============================================================================

def make_df():
    return pd.DataFrame({
        "DATE": ["2025-03-01", "2019-03-01", "2025-03-01"],
        "CLOSE": [1512625, 913000, 890000],
        "SYMBOL": ["Sydney", "Sydney", "Brisbane"],
    })


def make_iddf():
    """Df with row ids; the Brisbane row is off the display."""
    return make_df().assign(
        _id=["row-0", "row-1", "row-2"],
        in_view=[True, True, False],
    )


class TestResolveHighlightedNodes:
    def test_ids_become_nodes_keyed_by_id(self):
        nodes = resolve_highlighted_nodes(["row-0", "row-2"], make_iddf(), "DATE", "CLOSE", "SYMBOL")
        assert nodes == {
            "row-0": {"id": "row-0", "x": "2025-03-01", "y": 1512625, "SYMBOL": "Sydney"},
            "row-2": {"id": "row-2", "x": "2025-03-01", "y": 890000, "SYMBOL": "Brisbane"},
        }

    def test_invented_ids_are_dropped(self):
        assert resolve_highlighted_nodes(["row-99"], make_iddf(), "DATE", "CLOSE", "SYMBOL") == {}

    def test_no_id_column_means_no_highlights(self):
        assert resolve_highlighted_nodes(["row-0"], make_df(), "DATE", "CLOSE", "SYMBOL") == {}


class _Resp:
    def __init__(self, content):
        self.choices = [SimpleNamespace(message=SimpleNamespace(content=content))]


class TestAssignNodesToChunks:
    NODES = {"row-0": {"id": "row-0", "x": "2025-03-01", "y": 1512625},
             "row-1": {"id": "row-1", "x": "2019-03-01", "y": 913000}}

    def test_valid_indices_merged_invalid_ignored(self, monkeypatch):
        answer = {"assignments": [{"id": "row-0", "chunk": 1}, {"id": "row-1", "chunk": 7}]}
        monkeypatch.setattr(pp.client.chat.completions, "create", lambda **kw: _Resp(json.dumps(answer)))
        out = assign_nodes_to_chunks(self.NODES, ["First.", "Second."])
        assert out["row-0"]["chunk"] == 1
        assert "chunk" not in out["row-1"]        # out of range: blinks throughout

    def test_failure_leaves_nodes_unassigned(self, monkeypatch):
        def boom(**kw):
            raise RuntimeError("down")
        monkeypatch.setattr(pp.client.chat.completions, "create", boom)
        assert assign_nodes_to_chunks(self.NODES, ["One."]) == self.NODES


class TestGraphHighlights:
    """data_query_node takes ids from the answer; post_process_node places them."""

    def graph(self):
        import importlib
        return importlib.import_module("agent.graph")   # the package also exports a `graph` object

    def test_answer_ids_resolved_in_data_query_node(self, monkeypatch):
        g = self.graph()
        answer = json.dumps({"message": "Sydney peaked at 1,512,625 dollars.", "highlighted_ids": ["row-0", "made-up"]})
        monkeypatch.setattr(g, "_run_tool_loop", lambda **kw: answer)
        monkeypatch.setattr(g, "get_df", lambda: make_iddf())
        monkeypatch.setattr(g, "rewrite_long_node_lists_with_gpt", lambda t: t)
        out = g.data_query_node({"current_query": "when did Sydney peak?", "current_intent": "data_analysis",
                                 "x_field": "DATE", "y_field": "CLOSE", "color_field": "SYMBOL"})
        assert out["intent_responses"] == {"data_analysis": "Sydney peaked at 1,512,625 dollars."}
        assert list(out["nodes_by_id"]) == ["row-0"]

    def test_post_process_places_nodes_and_renumbers(self, monkeypatch):
        g = self.graph()
        text = "Sydney peaked at 1,512,625 dollars. Brisbane followed."
        seen = []
        monkeypatch.setattr(g, "assign_nodes_to_chunks",
                            lambda nodes, chunks: seen.append(chunks) or {k: {**v, "chunk": 0} for k, v in nodes.items()})
        out = g.post_process_node({"intent_responses": {"data_analysis": text}, "intents": [],
                                   "nodes_by_id": {"row-0": {"id": "row-0", "x": "2025-03-01", "y": 1512625}}})
        assert out["chunks"] == split_into_chunks(text) and seen == [out["chunks"]]
        assert out["nodes"] == {"node_1": {"id": "row-0", "x": "2025-03-01", "y": 1512625, "chunk": 0}}

    def test_non_data_intents_have_no_highlights(self, monkeypatch):
        g = self.graph()
        monkeypatch.setattr(g, "assign_nodes_to_chunks", lambda *a: pytest.fail("should not be called"))
        out = g.post_process_node({"intent_responses": {"chart_overview": "A chart."}, "intents": [],
                                   "nodes_by_id": {}})
        assert out["nodes"] == {}


class TestPublishMessage:
    """Every published payload carries chunks + message_id."""

    def publish(self, monkeypatch, **kwargs):
        import agent.mqtt_handler as mh
        published = []

        def fake_publish(topic, payload, qos=0, retain=False):
            published.append((topic, payload))
            return SimpleNamespace(rc=0)

        monkeypatch.setattr(mh, "_mqtt_client", SimpleNamespace(publish=fake_publish))
        mh.publish_message(**kwargs)
        assert len(published) == 1
        return json.loads(published[0][1].decode()
                          if isinstance(published[0][1], bytes) else published[0][1])

    def test_chunks_computed_when_not_passed(self, monkeypatch):
        body = self.publish(monkeypatch,
                            response_text="First point. Second point.")
        inner = body["agent_response_for_user"]
        assert inner["chunks"] == ["First point.", "Second point."]
        assert len(inner["message_id"]) == 32
        assert "nodes" not in inner
