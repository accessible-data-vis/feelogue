"""
Touch and highlight context handling.

Referent info lists are (timestamp_ms, text) pairs: Unity stamps every anchored
referent (touchdata.touch_timestamp, highlighted_context.highlight_timestamp),
and the enrichment orders them newest-first so the model can resolve singular
deixis ("this") to the latest referent while plural ("these") sees all of them.
A missing timestamp ranks oldest.
"""
from typing import Tuple, List, Dict

# Minimum probability threshold for touch detection
TOUCH_PROBABILITY_THRESHOLD = 0.2


def _timestamp_of(container: dict, key: str) -> float:
    try:
        return float(container.get(key) or 0)
    except (TypeError, ValueError):
        return 0.0


def _described_values(node: dict) -> dict:
    """A node's chart fields only, for the text the model reads. The row id stays
    in the node for highlight resolution but is never in text the model could speak."""
    nv = node.get("node_values", {}) or {}
    return {k: v for k, v in nv.items()
            if k != "_id" and not str(k).endswith(("_start", "_end", "_rtd_index"))}


def collect_touch_nodes(touchdata: dict) -> Tuple[List[Tuple[float, str]], Dict]:
    """
    Extract touched nodes from touch data.

    Returns:
        Tuple of ((timestamp_ms, human-readable info) list, nodes dict)
    """
    info, nodes_out = [], {}
    if not isinstance(touchdata, dict):
        return info, nodes_out

    for side in ["left_touch", "right_touch"]:
        block = touchdata.get(side)
        if not isinstance(block, dict):
            continue
        ts = _timestamp_of(block, "touch_timestamp")

        nodes = block.get("nodes", {}) or {}
        for node_id, node in nodes.items():
            if not isinstance(node, dict):
                continue
            if float(node.get("probability", 0) or 0) < TOUCH_PROBABILITY_THRESHOLD:
                continue

            # Categorize node type
            if "data-mark" in node_id:
                t = "Data Value"
            elif "x-axis" in node_id:
                t = "X-Axis Markup"
            elif "y-axis" in node_id:
                t = "Y-Axis Markup"
            else:
                t = "Unknown"

            nv = _described_values(node)
            info.append((ts,
                f"{side} - {t}: [{', '.join(map(str, nv.keys()))}], "
                f"Values: [{', '.join(map(str, nv.values()))}]"
            ))
            nodes_out[node_id] = node

    return info, nodes_out


def collect_highlight_nodes(highlighted_context: dict) -> Tuple[List[Tuple[float, str]], Dict]:
    """
    Extract highlighted nodes from highlight context.

    Returns:
        Tuple of ((timestamp_ms, human-readable info) list, nodes dict)
    """
    info, nodes_out = [], {}
    if not isinstance(highlighted_context, dict):
        return info, nodes_out

    ts = _timestamp_of(highlighted_context, "highlight_timestamp")
    nodes = highlighted_context.get("nodes") or {}
    if not isinstance(nodes, dict):
        return info, nodes_out

    for node_id, node in nodes.items():
        if not isinstance(node, dict):
            continue
        if float(node.get("probability", 0) or 0) < TOUCH_PROBABILITY_THRESHOLD:
            continue

        if "data-mark" in node_id:
            t = "Data Value"
        elif "x-axis" in node_id:
            t = "X-Axis Markup"
        elif "y-axis" in node_id:
            t = "Y-Axis Markup"
        else:
            t = "Unknown"

        nv = _described_values(node)
        info.append((ts,
            f"Highlighted - {t}: [{', '.join(map(str, nv.keys()))}], "
            f"Values: [{', '.join(map(str, nv.values()))}]"
        ))
        nodes_out[node_id] = node

    return info, nodes_out
