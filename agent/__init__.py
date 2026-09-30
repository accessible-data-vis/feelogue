"""
RTD Conversational Agent

A multimodal agent for accessible data visualization on refreshable tactile displays.
Combines touch and speech interaction for exploring charts and data.
"""

from .context import (
    get_df,
    get_current_config,
    update_dataframe_from_layer,
)

from .intent import classify_query, classify_intent

from .touch_context import (
    collect_touch_nodes,
    collect_highlight_nodes,
)

from .data_query import csv_query_tool

from .chart_loader import analyze_user_intent_with_context

from .postprocessing import (
    rewrite_long_node_lists_with_gpt,
    resolve_highlighted_nodes,
    split_into_chunks,
)

from .graph import graph

from .orchestrator import process_user_request

from .mqtt_handler import run, create_mqtt_client, publish_message

__version__ = "0.1.0"

__all__ = [
    # Context
    "get_df",
    "get_current_config",
    "update_dataframe_from_layer",
    # Intent
    "classify_query",
    "classify_intent",
    # Touch
    "collect_touch_nodes",
    "collect_highlight_nodes",
    # Data
    "csv_query_tool",
    # Chart loading
    "analyze_user_intent_with_context",
    # Post-processing
    "rewrite_long_node_lists_with_gpt",
    "resolve_highlighted_nodes",
    "split_into_chunks",
    # Graph
    "graph",
    # Orchestrator
    "process_user_request",
    # MQTT
    "run",
    "create_mqtt_client",
    "publish_message",
]
