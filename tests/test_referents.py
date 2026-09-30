"""Touch and highlight referents: ordered newest-first, the newest tagged, none dropped."""
import importlib

graph = importlib.import_module("agent.graph")
tc = importlib.import_module("agent.touch_context")


def _touch_block(node_id, values, ts, probability=1.0):
    return {
        "node_count": 1,
        "nodes": {node_id: {"node_xy": [10, 10], "node_values": values,
                            "probability": probability}},
        "touch_timestamp": ts,
    }


def _highlight_block(node_id, values, ts):
    return {
        "node_count": 1,
        "nodes": {node_id: {"node_xy": [20, 20], "node_values": values,
                            "probability": 1.0, "source": "navigation"}},
        "highlight_timestamp": ts,
    }


def _enrich(touchdata, highlighted_context, query="what is this?"):
    state = {"touchdata": touchdata, "highlighted_context": highlighted_context}
    return graph._enrich_query_with_referents(state, query)


def test_touch_newer_than_highlight_is_tagged_most_recent():
    q, patch = _enrich(
        {"left_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=2000)},
        _highlight_block("data-mark-2", {"PRICE": 700000}, ts=1000),
    )
    inside = q[q.index("(") + 1 : q.rindex(")")]
    parts = inside.split("; ")
    assert "left_touch" in parts[0] and parts[0].endswith("(most recent)")
    assert "Highlighted" in parts[1]
    assert patch["touch_used"] and patch["highlight_used"]


def test_highlight_newer_than_touch_wins_the_tag():
    # Touch B, then navigate to C: the navigation highlight is the latest and ranks first.
    q, _ = _enrich(
        {"right_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=1000)},
        _highlight_block("data-mark-2", {"PRICE": 700000}, ts=2000),
    )
    inside = q[q.index("(") + 1 : q.rindex(")")]
    parts = inside.split("; ")
    assert "Highlighted" in parts[0] and parts[0].endswith("(most recent)")
    assert "right_touch" in parts[1]


def test_both_referents_always_survive():
    # No eviction: the compare question needs every anchor present.
    q, patch = _enrich(
        {"left_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=2000)},
        _highlight_block("data-mark-2", {"PRICE": 700000}, ts=1000),
        query="average between these?",
    )
    assert "left_touch" in q and "Highlighted" in q
    assert len(patch["touch_nodes"]) == 1 and len(patch["highlight_nodes"]) == 1


def test_single_referent_gets_no_tag():
    q, _ = _enrich(
        {"left_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=2000)},
        {},
    )
    assert "(most recent)" not in q


def test_missing_timestamp_ranks_oldest():
    legacy = _touch_block("data-mark-1", {"PRICE": 900000}, ts=None)
    del legacy["touch_timestamp"]
    q, _ = _enrich(
        {"left_touch": legacy},
        _highlight_block("data-mark-2", {"PRICE": 700000}, ts=1000),
    )
    inside = q[q.index("(") + 1 : q.rindex(")")]
    assert inside.split("; ")[0].startswith("Highlighted")


def test_probability_filter_still_applies():
    # The 0.2 probability gate, as in Unity.
    q, patch = _enrich(
        {"left_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=2000,
                                    probability=0.05)},
        {},
    )
    assert "left_touch" not in q
    assert not patch["touch_used"]


def test_two_hands_are_two_parts():
    q, _ = _enrich(
        {"left_touch": _touch_block("data-mark-1", {"PRICE": 900000}, ts=1000),
         "right_touch": _touch_block("data-mark-2", {"PRICE": 700000}, ts=2000)},
        {},
    )
    inside = q[q.index("(") + 1 : q.rindex(")")]
    parts = inside.split("; ")
    assert len(parts) == 2
    assert "right_touch" in parts[0] and parts[0].endswith("(most recent)")
