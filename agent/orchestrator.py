"""
Entry points into the graph: a user request from Unity, and the held rest of a
request that loaded a chart. Routing and post-processing live in graph.py.
"""
import json

from .graph import graph
from .context import get_current_config


def process_user_request(user_input: str) -> dict:
    """Parse an incoming MQTT payload and invoke the LangGraph graph."""
    data = json.loads(user_input)
    if "user_request_for_agent" not in data:
        return {
            "response": "Invalid payload received.",
            "rtd_command": None,
            "nodes": None,
            "referents": None,
            "followup_stage": False,
        }

    user_request = data["user_request_for_agent"]
    transcript_data = user_request.get("transcript", {})
    user_query = (
        transcript_data.get("text_transcript")
        or transcript_data.get("transcript")
        or ""
    ).strip()

    state_patch = {
        "user_query": user_query,
        "touchdata": user_request.get("touchdata") or {},
        "highlighted_context": user_request.get("highlighted_context") or {},
        # Presentation layer on the display ({"layer": ..., "series": ...}) or None
        "presentation": user_request.get("presentation") or None,
        "preset_intents": None,
    }

    return _reply(graph.invoke(state_patch, get_current_config()))


def process_held_request(pieces: list[dict]) -> dict:
    """Run the held rest of a request once its chart's data has arrived. The pieces
    are already-classified intents."""
    state_patch = {
        "user_query": " and ".join(p.get("query", "") for p in pieces),
        "touchdata": {},
        "highlighted_context": {},
        "presentation": None,
        "preset_intents": pieces,
    }
    return _reply(graph.invoke(state_patch, get_current_config()))


def _reply(result: dict) -> dict:
    return {
        "response": result.get("final_response", ""),
        "rtd_command": result.get("rtd_command"),
        "nodes": result.get("nodes") or None,
        "chunks": result.get("chunks") or None,
        "referents": {
            "touch_used": result.get("touch_used", False),
            "highlight_used": result.get("highlight_used", False),
            "touch_nodes": result.get("touch_nodes") or {},
            "highlight_nodes": result.get("highlight_nodes") or {},
        },
        "followup_stage": result.get("followup_stage", False),
        "presentation": result.get("presentation_command"),
    }
