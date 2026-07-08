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
    # Conversation history — persisted across turns via MemorySaver
    messages: Annotated[list[BaseMessage], add_messages]

    # Chart / dataset metadata — persisted, set by MQTT via graph.update_state()
    chart_type: Optional[str]
    data_name: Optional[str]
    x_field: Optional[str]
    y_field: Optional[str]
    color_field: Optional[str]
    df_columns: list[str]
    chart_overview: Optional[object]
    chart_metadata_index: Optional[dict]
    image_data: Optional[str]       # base64 PNG, persisted for repeated image_analysis calls
    image_format: Optional[str]
    dataset_version: int
    active_layer: Optional[str]
    vega_lite_schema: Optional[str]

    # Follow-up / disambiguation — persisted so next turn sees pending state
    followup_stage: bool
    followup_topic: Optional[str]
    pending_chart_options: list[dict]

    # Deictic referent memory — persisted for "zoom here"-style follow-up ops
    last_touch_node_values: Optional[dict]
    last_referent_node_values: Optional[dict]

    # Turn-scoped inbound payload — reset by input_node each invocation
    user_query: str
    touchdata: dict
    highlighted_context: dict

    # Turn-scoped classification results — reset by classifier_node
    intents: list[dict]             # [{type: str, query: str}]
    has_deictic: bool
    intent_index: int
    current_intent: str
    current_query: str

    # Per-intent response accumulator — uses _merge_dict_or_reset so None resets to {}
    intent_responses: Annotated[dict, _merge_dict_or_reset]

    # Turn-scoped output fields — reset by input_node, replace semantics
    rtd_command: Optional[dict]
    nodes: dict
    touch_used: bool
    highlight_used: bool
    touch_nodes: dict
    highlight_nodes: dict

    # Final assembled response — written by post_process_node
    final_response: str
