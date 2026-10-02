"""
AgentState TypedDict for the LangGraph graph.
"""
from __future__ import annotations
from typing import Annotated, Optional
from typing_extensions import TypedDict
from langchain_core.messages import BaseMessage
from langgraph.graph.message import add_messages


def _merge_dict_or_reset(a: dict | None, b: dict | None) -> dict:
    """Merge two dicts. b=None resets to empty dict."""
    if b is None:
        return {}
    return {**(a or {}), **b}


class AgentState(TypedDict):
    # Conversation history: persisted across turns via MemorySaver
    messages: Annotated[list[BaseMessage], add_messages]

    # Chart / dataset metadata: persisted, set by MQTT via graph.update_state()
    chart_type: Optional[str]
    data_name: Optional[str]
    x_field: Optional[str]
    y_field: Optional[str]
    color_field: Optional[str]
    df_columns: list[str]
    chart_overview: Optional[object]
    chart_metadata_index: Optional[dict]
    image_data: Optional[str]       # base64 PNG of the chart from Unity, read by chart_image_tool
    image_format: Optional[str]
    active_layer: Optional[str]
    vega_lite_schema: Optional[str]
    display_marks: Optional[list]   # how the display draws each series: [{name, symbol}] or, for bars, [{name, texture}]

    # Follow-up / disambiguation: persisted so next turn sees pending state
    followup_stage: bool
    followup_topic: Optional[str]
    pending_chart_options: list[dict]
    hidden_series: Optional[list[str]]   # series hidden by a spoken filter

    # Turn-scoped inbound payload: reset by input_node each invocation
    user_query: str
    touchdata: dict
    highlighted_context: dict
    presentation: Optional[dict]    # {"layer": "title"|"x_axis"|"y_axis"|"series"|"data"|"summary", "series": ...} or None

    # Held rest of an earlier request, run instead of classifying (set by
    # process_held_request, cleared by classifier_node)
    preset_intents: Optional[list[dict]]

    # Turn-scoped classification results: reset by classifier_node
    intents: list[dict]             # [{type: str, query: str}]
    has_deictic: bool
    intent_index: int
    current_intent: str
    current_query: str

    # Per-intent response accumulator: uses _merge_dict_or_reset so None resets to {}
    intent_responses: Annotated[dict, _merge_dict_or_reset]

    # Turn-scoped output fields: reset by input_node, replace semantics
    rtd_command: Optional[dict]
    nodes: dict
    nodes_by_id: Annotated[dict, _merge_dict_or_reset]   # this turn's highlight nodes, by row id
    chunks: list                    # spoken sentence chunks of final_response; nodes reference them by index
    touch_used: bool
    highlight_used: bool
    touch_nodes: dict
    highlight_nodes: dict
    presentation_command: Optional[str]   # "start" (overview request), "skip" (load with more to run), or None
    held_intents: list[dict]        # pieces after a load, for load_chart_node to hold
    held_note: Optional[str]        # "I didn't do the rest...", appended to the reply

    # Final assembled response: written by post_process_node
    final_response: str
