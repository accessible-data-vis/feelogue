"""
Runtime state shared across modules that stays out of AgentState: the live
DataFrame (MemorySaver can't serialize pandas objects), the graph thread id,
generated presentation text, and the held rest of a request that loads a chart.
"""
import threading

import pandas as pd

# Live chart data, never put into AgentState
_df: pd.DataFrame | None = None

# Current LangGraph thread ID, fixed for the process lifetime. Context is
# reset (except `messages` and `chart_metadata_index`) on every rtd_data_for_agent
# message instead of rotating to a new thread; see reset_context_keep_messages().
_current_thread_id: str = "default"


# Presentation text generated for charts with no authored overview, keyed by
# (data_name, digest of the chart's data): a reload reuses it, edited data doesn't.
_generated_overviews: dict[tuple[str, str], dict] = {}


def get_generated_overview(data_name: str, data_digest: str) -> dict | None:
    return _generated_overviews.get((data_name, data_digest))


def set_generated_overview(data_name: str, data_digest: str, overview: dict) -> None:
    _generated_overviews[(data_name, data_digest)] = overview


def get_df() -> pd.DataFrame | None:
    return _df


# The rest of a request that loads a chart ("open airfares and hide Sydney") waits
# here for the new chart's data. It lives outside AgentState because a load resets that.
# Stages: awaiting_choice (asked which chart), awaiting_load, awaiting_data.
_held_lock = threading.Lock()
_held = {"pieces": [], "stage": None, "gen": 0}


def hold_pieces(pieces: list[dict], stage: str) -> int:
    """Hold intents at a stage and return the hold's generation number."""
    with _held_lock:
        _held["pieces"], _held["stage"] = list(pieces), stage
        _held["gen"] += 1
        return _held["gen"]


def held_stage() -> str | None:
    return _held["stage"]


def held_pieces() -> list[dict]:
    return list(_held["pieces"])


def held_generation() -> int:
    """Generation of the current hold, so a timer can tell its hold was replaced."""
    return _held["gen"]


def advance_held(from_stage: str, to_stage: str) -> bool:
    """Move the hold to to_stage; False if it wasn't at from_stage."""
    with _held_lock:
        if _held["stage"] != from_stage:
            return False
        _held["stage"] = to_stage
        return True


def take_held(generation: int | None = None) -> list[dict]:
    """Remove and return the held pieces; with a generation, only if the hold is still that one."""
    with _held_lock:
        if generation is not None and generation != _held["gen"]:
            return []
        pieces = _held["pieces"]
        _held["pieces"], _held["stage"] = [], None
        return pieces


def describe_pieces(pieces: list[dict]) -> str:
    """The held pieces in the user's words, for the "I didn't do" reply."""
    return " and ".join(p.get("query") or p.get("type", "") for p in pieces)


# Cached by (data, filter) so the pandas executor, which is cached on the
# frame's identity, gets the same object back.
_question_frame_key = None
_question_frame: pd.DataFrame | None = None


def frame_for_questions(state: dict) -> pd.DataFrame | None:
    """The live DataFrame plus a `hidden_by_filter` column (series the user hid, from the
    agent's own filter). No column during the presentation, which shows the whole chart."""
    global _question_frame_key, _question_frame
    df = _df
    color = state.get("color_field")
    hidden = tuple(state.get("hidden_series") or [])
    if df is None or not color or color not in df.columns or not hidden or state.get("presentation"):
        return df
    key = (df, color, hidden)
    if (_question_frame_key is None or _question_frame_key[0] is not df
            or _question_frame_key[1:] != key[1:]):
        frame = df.copy()
        frame["hidden_by_filter"] = frame[color].astype(str).isin(hidden)
        _question_frame_key, _question_frame = key, frame
    return _question_frame


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

    from .date_cast import sync_datetime_columns, is_shadow_col
    df = sync_datetime_columns(df, x_field=x_field)
    _df = df

    from .graph import graph

    metadata_patch = {
        "x_field": x_field,
        "y_field": y_field,
        "color_field": msg.get("series_field"),
        "df_columns": [c for c in df.columns if not is_shadow_col(c) and c != "_id"],
        "chart_type": chart_type,
        "active_layer": layer_name,
    }

    graph.update_state(get_current_config(), metadata_patch)

    print(f"DataFrame updated: {len(df)} rows, columns: {list(df.columns)}")
    return metadata_patch
