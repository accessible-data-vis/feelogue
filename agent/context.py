"""
Runtime state shared across modules.

agent_context has been replaced by AgentState in graph.py.
This module now holds only the live DataFrame (excluded from LangGraph
checkpoints because pandas objects can't be serialized by MemorySaver)
and the current thread ID used to address the graph checkpoint.
"""
import pandas as pd

# Module-level DataFrame ref -- never put into AgentState
_df: pd.DataFrame | None = None

# Current LangGraph thread ID -- fixed for the process lifetime. Context is
# reset (except `messages` and `chart_metadata_index`) on every rtd_data_for_agent
# message instead of rotating to a new thread; see reset_context_keep_messages().
_current_thread_id: str = "default"


def get_df() -> pd.DataFrame | None:
    return _df


def get_current_config() -> dict:
    return {
        "configurable": {"thread_id": _current_thread_id},
        "recursion_limit": 30,
    }
def reset_context_keep_messages() -> None:
    """Clear every AgentState field except `messages` and `chart_metadata_index`
    on a new chart load.

    chart_metadata_index is a cross-chart catalog (set once at boot), not
    per-chart data, so it's exempted here. Everything else is expected to be
    repopulated by the rtd_data_for_agent patch that follows this call, and
    by the next layer_data_update. Iterates AgentState's own annotations
    (rather than a hardcoded field list) so newly added fields are swept up
    automatically.
    """
    from .graph import graph
    from .state import AgentState

    keep = {"messages", "chart_metadata_index"}
    reset_patch = {k: None for k in AgentState.__annotations__ if k not in keep}
    graph.update_state(get_current_config(), reset_patch)
    print("[context] Context reset (messages, chart_metadata_index kept) for new chart load")


def update_dataframe_from_layer(msg: dict) -> dict:
    """
    Build a DataFrame from a layer_data_update MQTT message.
    Updates the module-level df ref and returns a serializable metadata
    patch suitable for graph.update_state().
    """
    global _df

    layer_name = msg.get("layer_name", "unnamed")
    chart_type = msg.get("chart_type", "line")
    data_points = msg.get("data_points") or msg.get("data") or []

    if not data_points:
        print(f"Warning: No data points in layer update for '{layer_name}'")
        return {}

    x_field = msg.get("x_field")
    y_field = msg.get("y_field")

    if not x_field or not y_field:
        sample = data_points[0]
        keys = list(sample.keys())
        if not x_field:
            for k in keys:
                kl = k.lower()
                if kl in ("x", "date", "time", "year", "quarter", "month", "period"):
                    x_field = k
                    break
            if not x_field and len(keys) >= 1:
                x_field = keys[0]
        if not y_field:
            for k in keys:
                kl = k.lower()
                if kl in ("y", "value", "amount", "count", "rate"):
                    y_field = k
                    break
            if not y_field and len(keys) >= 2:
                y_field = keys[1]

    df = pd.DataFrame(data_points)
    _df = df

    from .graph import graph

    metadata_patch = {
        "x_field": x_field,
        "y_field": y_field,
        "color_field": msg.get("series_field"),
        "df_columns": list(df.columns),
        "chart_type": chart_type,
        "active_layer": layer_name,
    }

    graph.update_state(get_current_config(), metadata_patch)

    print(f"DataFrame updated: {len(df)} rows, columns: {list(df.columns)}")
    return metadata_patch
