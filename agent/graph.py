"""
LangGraph graph for the Feelogue agent: classify a request, route each intent
to its node, and assemble the reply. Every LLM call sees the conversation history.
"""
import re
import time

from langchain_core.messages import HumanMessage, AIMessage, SystemMessage, ToolMessage
from langchain_openai import ChatOpenAI
from langgraph.graph import StateGraph, END
from langgraph.checkpoint.memory import MemorySaver

from .config import OPENAI_MODEL_ANALYSIS
from .state import AgentState
from .data_query import csv_query_tool
from .image_tool import chart_image_tool
from .intent import classify_query
from .prompts import (
    get_data_query_scope,
    get_data_query_system_prompt,
)
from .chart_loader import analyze_user_intent_with_context
from .postprocessing import (
    parse_data_query_response,
    resolve_highlighted_nodes,
    assign_nodes_to_chunks,
    rewrite_long_node_lists_with_gpt,
    combine_multi_intent_responses,
    split_into_chunks,
)
from .touch_context import collect_touch_nodes, collect_highlight_nodes
from .utils import strip_markdown
from .context import (get_df, frame_for_questions, hold_pieces, held_pieces, held_stage,
                      take_held, describe_pieces)
from .schema import DATA_QUERY_SCHEMA
from .filtering import handle_filter_request


# =============================================================================
# LLM + tool setup
# =============================================================================

_main_llm = ChatOpenAI(model=OPENAI_MODEL_ANALYSIS, temperature=0)
_tools = [csv_query_tool, chart_image_tool]
_tools_by_name = {t.name: t for t in _tools}
_llm_with_tools = _main_llm.bind_tools(_tools)
# The data-query loop answers in a schema: the spoken message plus the `_id`s of
# the rows it is anchored to, so highlights come from the answer itself.
_DATA_QUERY_FORMAT = {
    "type": "json_schema",
    "json_schema": {"name": "data_query_response", "schema": DATA_QUERY_SCHEMA},
}
_llm_data_query = _main_llm.bind_tools(_tools, strict=True).bind(response_format=_DATA_QUERY_FORMAT)
# Last round: tool_choice="none" keeps the tool history valid but forces an answer.
_llm_data_query_final = _main_llm.bind_tools(_tools, strict=True, tool_choice="none").bind(
    response_format=_DATA_QUERY_FORMAT)
_max_iter : int = 6 # number of iteration that data query is allowed to run

def _run_tool_loop(state: AgentState , enriched_query:str, max_iterations: int = 6, llm=None,
                   final_llm=None) -> str:
    """Synchronous tool-calling loop. Returns the final text response.

    `state` is merged into each tool call's args so tools annotated with
    InjectedState receive it: only LangGraph's ToolNode fills that in, and this
    loop doesn't use it. The last round runs on `final_llm`, which can't call tools.
    """
    import pandas as pd
    

    df = frame_for_questions(state)
    state["current_query"] = enriched_query

    # Build df_context for system prompt
    from .date_cast import is_shadow_col
    df_cols = state.get("df_columns") or []
    n = len(df) if isinstance(df, pd.DataFrame) else 0
    preview_df = df
    if isinstance(df, pd.DataFrame):
        # Live columns stay out of the preview: they change within a chart and
        # would break the cached prefix.
        preview_df = df[[c for c in df.columns if not is_shadow_col(c)
                         and c not in ("_id", "in_view", "hidden_by_filter")]]

    if n <= 10:  # head + tail would cover everything anyway
        sample = {
            "note": "COMPLETE - these are ALL the rows; no hidden data",
            "rows": preview_df.to_dict(orient="records") if n else [],
        }
    else:
        sample = {
            "note": "TRIMMED - first and last 5 rows only; more data hidden in between",
            "head": preview_df.head(5).to_dict(orient="records"),
            "tail": preview_df.tail(5).to_dict(orient="records"),
        }

    df_context = {
        "schema": {
            "n_rows": n,
            "columns": df_cols,
            "x_field": state.get("x_field"),
            "y_field": state.get("y_field"),
            "color_field": state.get("color_field"),
        },
        "sample_rows": sample,
    }
    # The system prompt stays identical across turns so the provider's prompt
    # cache covers it and the history. Per-turn scope goes in its own message
    # after the history.
    stable_system_prompt = SystemMessage(content=get_data_query_system_prompt(
        df_context,
        data_name=state.get("data_name") or "the current dataset",
        x_field=state.get("x_field") or "x-axis",
        y_field=state.get("y_field") or "y-axis",
        color_field=state.get("color_field"),
        df=df,
        vega_lite_schema=state.get("vega_lite_schema"),
    ))
    scope = get_data_query_scope(df, state.get("hidden_series"), state.get("presentation"))
    context_messages = [SystemMessage(content="**Data Scope**:\n" + scope)] if scope else []

    # Append-only: the budget rides on the last message instead of a separate
    # one, so each round's request starts with the previous round's in full.
    msgs_for_llm = ([stable_system_prompt] + list(state.get("messages", [])) + context_messages
                    + [HumanMessage(content=enriched_query + _budget_notice(max_iterations - 1))])

    for i in range(max_iterations):
        is_final = i == max_iterations - 1
        round_llm = (final_llm or _main_llm) if is_final else (llm or _llm_with_tools)
        response = round_llm.invoke(msgs_for_llm)
        _log_cache_hit(f"{state.get('current_intent', 'tool_loop')} round {i + 1}", response)
        msgs_for_llm.append(response)
        if not response.tool_calls:
            return response.content or ""
        for tc in response.tool_calls:
            result = _tools_by_name[tc["name"]].invoke({**tc["args"], "state": state})
            msgs_for_llm.append(ToolMessage(content=str(result), tool_call_id=tc["id"]))
        if not is_final:
            last = msgs_for_llm[-1]
            msgs_for_llm[-1] = ToolMessage(content=last.content + _budget_notice(max_iterations - 2 - i),
                                           tool_call_id=last.tool_call_id)
    return msgs_for_llm[-1].content or ""


def _budget_notice(rounds_left: int) -> str:
    """The round budget, appended to the last message."""
    if rounds_left <= 0:
        body = ("WARNING: This is your FINAL iteration. You MUST answer now using only what "
                "you already know from prior tool results. Do NOT call any tools.")
    else:
        body = f"Iterations remaining: {rounds_left}. Break down the problem and evaluate each step carefully."
    return f"\n\n[SYSTEM NOTICE] {body}"


def _log_cache_hit(label: str, response) -> None:
    """Print how much of the prompt came from the provider's cache. A drop to 0 means
    something turn-varying got into the prefix (or the cache expired)."""
    usage = getattr(response, "usage_metadata", None) or {}
    total = usage.get("input_tokens")
    if not total:
        return
    cached = (usage.get("input_token_details") or {}).get("cache_read", 0)
    print(f"[{label}] prompt cache: {cached}/{total} input tokens ({100 * cached / total:.0f}%)")


# =============================================================================
# Node: input_node
# =============================================================================

def input_node(state: AgentState) -> dict:
    """Entry point of langgraph"""
    print(f"[input_node],message length: {len(state.get("messages"))}")
    return {
        "rtd_command": None,
        "nodes": {},
        "nodes_by_id": None,   # None resets the per-turn accumulator
        "chunks": [],
        "touch_used": False,
        "highlight_used": False,
        "touch_nodes": {},
        "highlight_nodes": {},
        "presentation_command": None,
        "held_intents": [],
        "held_note": None,
        "final_response": "",
    }


# =============================================================================
# Node: classifier_node
# =============================================================================

# A pointing word followed by a word for the chart itself.
_CHART_PHRASE = re.compile(
    r"\b(this|that|these|those)\s+(chart|graph|plot|figure|diagram|visuali[sz]ation|data(set)?)s?\b",
    re.IGNORECASE,
)


def classifier_node(state: AgentState) -> dict:
    """Classify intents and detect deictic references. Runs held pieces as given,
    and holds everything after a load for load_chart_node."""
    user_query = state.get("user_query", "")
    preset = state.get("preset_intents")
    if preset:
        # The held rest of an earlier request, now that its chart has loaded.
        intents = [dict(i) for i in preset]
        print(f"[classifier_node] held pieces {[i['type'] for i in intents]}")
        return {
            "intents": intents,
            "has_deictic": False,
            "intent_index": 0,
            "current_intent": intents[0]["type"],
            "current_query": intents[0]["query"],
            "intent_responses": None,
            "preset_intents": None,
        }
    messages = state.get("messages", [])
    result = classify_query(user_query, messages=messages)
    intents = result["intents"]
    has_deictic = result["has_deictic"]

    # "What value is this" with an anchored referent is a question about that point,
    # not the chart. The model's has_deictic flag flickers, so a word test backs it
    # up; "this chart" / "that graph" name the chart and don't count.
    without_chart = _CHART_PHRASE.sub(" ", user_query)
    names_chart = without_chart != user_query
    looks_deictic = bool(re.search(r"\b(this|these|that|those|here|it)\b", without_chart, re.IGNORECASE))
    if (has_deictic and not names_chart) or looks_deictic:
        _, touch_nodes = collect_touch_nodes(state.get("touchdata") or {})
        _, highlight_nodes = collect_highlight_nodes(state.get("highlighted_context") or {})
        if touch_nodes or highlight_nodes:
            for intent in intents:
                if intent["type"] == "chart_overview":
                    print(f"[classifier_node] deictic + anchored referent: {intent['type']} -> data_analysis")
                    intent["type"] = "data_analysis"

    print(f"[classifier_node] {[i['type'] for i in intents]}")

    # A request that loads a chart does only the load now; the rest waits for the
    # new chart's data (load_chart_node decides what to hold).
    held_now, note = [], None
    load = next((i for i in intents if i["type"] == "load_chart"), None)
    if load is not None and len(intents) > 1:
        held_now = [i for i in intents if i is not load]
        intents = [load]
    if load is None and held_stage():
        # The user moved on before a chart was loaded for the held pieces.
        note = f"I didn't do the rest of your earlier request, {describe_pieces(take_held())}, since no chart was loaded."

    first = intents[0] if intents else {"type": "general_question", "query": user_query}
    return {
        "intents": intents,
        "has_deictic": has_deictic,
        "intent_index": 0,
        "current_intent": first["type"],
        "current_query": first["query"],
        "intent_responses": None,   # None triggers _merge_dict_or_reset to clear to {}
        "held_intents": held_now,
        "held_note": note,
    }


# =============================================================================
# Routing helpers (conditional edge functions)
# =============================================================================

_INTENT_NODE_MAP = {
    "load_chart":     "load_chart_node",
    "chart_overview": "chart_overview_node",
    "filter":         "filter_node",
}


def route_intent(state: AgentState) -> str:
    """The node for the current intent; anything unmapped goes to data_query_node."""
    intent = state.get("current_intent", "general_question")
    destination = _INTENT_NODE_MAP.get(intent, "data_query_node")
    print(f"[route_intent] -> {destination}")
    return destination


def loop_or_finish(state: AgentState) -> str:
    """Next intent if any remain, else post-processing."""
    next_idx = state.get("intent_index", 0) + 1
    total = len(state.get("intents", []))
    if next_idx < total:
        return "advance_intent_node"
    return "post_process_node"


# =============================================================================
# Node: advance_intent_node
# =============================================================================

def advance_intent_node(state: AgentState) -> dict:
    """Bump intent_index and inject the prior result into remaining intent queries."""
    idx = state["intent_index"]
    intents = list(state["intents"])
    prior_type = intents[idx]["type"]
    prior_response = state.get("intent_responses", {}).get(prior_type, "")

    next_idx = idx + 1
    for i in range(next_idx, len(intents)):
        prefix = (
            f"From the same query, we already used {prior_type} and got: "
            f"{prior_response}. Use this if needed: "
        )
        intents[i] = {**intents[i], "query": prefix + intents[i].get("query", "")}

    next_intent = intents[next_idx]
    print(f"[advance_intent_node] -> {next_intent['type']}")
    return {
        "intent_index": next_idx,
        "intents": intents,
        "current_intent": next_intent["type"],
        "current_query": next_intent["query"],
    }


# =============================================================================
# Node: load_chart_node
# =============================================================================

def load_chart_node(state: AgentState) -> dict:
    """Resolve and load a chart by name, with disambiguation if needed."""
    print(f"[load_chart_node]")
    analysis = analyze_user_intent_with_context(state.get("current_query", ""), state)
    followup = analysis.get("followup_stage", False)
    rtd = analysis.get("rtd_command")

    # This request's other pieces, plus any held from a request whose "which chart?"
    # question this turn answers. mqtt_handler runs them when the new chart's data arrives.
    pending = held_pieces() + list(state.get("held_intents") or [])
    presentation, note = None, None
    if pending:
        if isinstance(rtd, str) and rtd:
            if all(p["type"] == "chart_overview" for p in pending):
                take_held()        # the load starts the presentation anyway
            else:
                hold_pieces(pending, "awaiting_load")
                presentation = "skip"   # the held pieces run instead
        elif followup:
            hold_pieces(pending, "awaiting_choice")
        else:
            take_held()
            note = f"I didn't do the rest of your request, {describe_pieces(pending)}, since no chart was loaded."

    return {
        "intent_responses": {state["current_intent"]: analysis["response"]},
        "rtd_command": rtd,
        "followup_stage": followup,
        "followup_topic": "load_chart" if followup else None,
        "pending_chart_options": analysis.get("pending_chart_options", []),
        "presentation_command": presentation,
        "held_note": note,
    }


# =============================================================================
# Node: filter_node
# =============================================================================

def filter_node(state: AgentState) -> dict:
    """Show or hide whole series ("filter everything apart from Storage")."""
    print(f"[filter_node]")
    df = get_df()
    color_field = state.get("color_field")
    all_series = []
    if df is not None and color_field and color_field in df.columns:
        all_series = [str(s) for s in dict.fromkeys(df[color_field].dropna())]
    # During the presentation a filter is always sent, even when it changes nothing
    # here: it ends the presentation, and the isolated layer is what the user feels.
    rtd_cmd, hidden, reply = handle_filter_request(
        state.get("current_query", ""), all_series, state.get("hidden_series") or [],
        force_send=bool(state.get("presentation")))
    return {
        "intent_responses": {state["current_intent"]: reply},
        "rtd_command": rtd_cmd,
        "hidden_series": hidden,
        "followup_stage": False,
    }


# =============================================================================
# Node: chart_overview_node
# =============================================================================

def chart_overview_node(state: AgentState) -> dict:
    """Start the chart's layered presentation in Unity, which speaks each layer itself."""
    print(f"[chart_overview_node]")
    if not state.get("chart_type") or not state.get("df_columns"):
        return {
            "intent_responses": {state["current_intent"]: "I don't have a chart loaded yet. Please load a chart first."},
            "followup_stage": False,
        }
    return {
        "intent_responses": {state["current_intent"]: "Here's the overview."},
        "presentation_command": "start",
        "followup_stage": False,
    }


# =============================================================================
# Node: data_query_node
# =============================================================================

def _enrich_query_with_referents(state: AgentState, query: str) -> tuple[str, dict]:
    """Append the anchored referents (touches and the navigation highlight) to the
    query, newest first, the newest tagged so singular "this" resolves to it while
    plural "these" uses them all. Returns (enriched query, state patch)."""
    touch_info, touch_nodes = collect_touch_nodes(state.get("touchdata", {}))
    highlight_info, highlight_nodes = collect_highlight_nodes(state.get("highlighted_context", {}))
    use_touch = len(touch_nodes) > 0
    use_highlight = len(highlight_nodes) > 0

    timed_parts = []
    if use_touch:
        timed_parts.extend(touch_info)
    if use_highlight:
        timed_parts.extend(highlight_info)
    timed_parts.sort(key=lambda p: p[0], reverse=True)
    referent_parts = [text for _, text in timed_parts]
    if len(referent_parts) > 1:
        referent_parts[0] += " (most recent)"

    enriched_query = f"{query} ({'; '.join(referent_parts)})" if referent_parts else query
    return enriched_query, {
        "touch_used": use_touch,
        "highlight_used": use_highlight,
        "touch_nodes": touch_nodes if use_touch else {},
        "highlight_nodes": highlight_nodes if use_highlight else {},
    }


def data_query_node(state: AgentState) -> dict:
    """
    Handle data_analysis, trend, touch_interaction, and general_question intents.
    Enriches the query with touch/highlight context, runs the inline tool loop,
    then post-processes the response.
    """
    query = state.get("current_query", "")
    print(f"[data_query_node] intent={state.get('current_intent')!r}")
    enriched_query, referent_patch = _enrich_query_with_referents(state, query)

    start_time = time.perf_counter()
    raw = _run_tool_loop(state=state, enriched_query=enriched_query, llm=_llm_data_query,
                         final_llm=_llm_data_query_final)
    elapsed = time.perf_counter() - start_time
    print(f"[data_query_node] resolved in {elapsed:.2f}s (intent={state.get('current_intent')!r})")

    print(f"response from tool loop: \n {raw}")
    result = parse_data_query_response(raw)
    # Post-processing
    response_text = strip_markdown(result.get("message") or "")
    rewritten = rewrite_long_node_lists_with_gpt(response_text)
    if rewritten != response_text:
        response_text = rewritten

    print(f"[data_query_node] response={response_text!r}")

    # Only ids in the live dataframe survive (the model can invent ids). Keyed by id so
    # several intents in one turn accumulate; post_process_node places them on sentences.
    resolved = resolve_highlighted_nodes(
        result.get("highlighted_ids") or [], get_df(),
        state.get("x_field"), state.get("y_field"), state.get("color_field"))
    print(f"[data_query_node] resolved nodes: {resolved}")
    return {
        "intent_responses": {state["current_intent"]: response_text},
        "nodes_by_id": resolved,
        **referent_patch,
        "followup_stage": False,
    }


# =============================================================================
# Node: post_process_node
# =============================================================================

# Intents whose responses cite data points worth highlighting (the set
# data_query_node handles).
_DATA_INTENTS = {"data_analysis", "trend", "touch_interaction", "general_question"}


def post_process_node(state: AgentState) -> dict:
    """Assemble the final response, split it into spoken chunks, and extract
    highlight nodes against the final text (with per-chunk assignment)."""
    intent_responses = state.get("intent_responses") or {}
    print(f"[post_process_node]")

    if len(intent_responses) > 1:
        final_response = combine_multi_intent_responses(responses=intent_responses,query=state.get("user_query"))
    elif len(intent_responses) == 1:
        final_response = next(iter(intent_responses.values()))
    else:
        final_response = "I'm not sure how to help with that."

    # A held request that couldn't run says so at the end of this reply.
    if state.get("held_note"):
        final_response = f"{final_response} {state['held_note']}"

    # Unity plays these chunks verbatim. A follow-up question stays one chunk: Unity
    # opens the mic only after the last chunk is spoken.
    chunks = [final_response] if state.get("followup_stage") else split_into_chunks(final_response)

    nodes = {}
    raw_nodes = state.get("nodes_by_id") or {}
    if raw_nodes and any(intent in _DATA_INTENTS for intent in intent_responses):
        # Place each resolved point on the sentence that mentions it, then
        # renumber the id-keyed accumulator into the wire's node_1/node_2/... keys.
        placed = assign_nodes_to_chunks(raw_nodes, chunks)
        nodes = {f"node_{i}": n for i, n in enumerate(placed.values(), start=1)}

    return {
        "final_response": final_response,
        "nodes": nodes,
        "chunks": chunks,
        "messages": [
        HumanMessage(
            content=state.get("user_query") or state.get("current_query", ""),
            metadata={"intent": [i["type"] for i in state.get("intents", [])]}
        ),
        AIMessage(content=final_response)],
    }


# =============================================================================
# Build and compile the graph
# =============================================================================

def _build_graph():
    builder = StateGraph(AgentState)

    builder.add_node("input_node", input_node)
    builder.add_node("classifier_node", classifier_node)
    builder.add_node("advance_intent_node", advance_intent_node)
    builder.add_node("load_chart_node", load_chart_node)
    builder.add_node("chart_overview_node", chart_overview_node)
    builder.add_node("filter_node", filter_node)
    builder.add_node("data_query_node", data_query_node)
    builder.add_node("post_process_node", post_process_node)

    builder.set_entry_point("input_node")
    builder.add_edge("input_node", "classifier_node")

    handler_nodes = {
        "load_chart_node":     "load_chart_node",
        "chart_overview_node": "chart_overview_node",
        "filter_node":         "filter_node",
        "data_query_node":     "data_query_node",
    }
    builder.add_conditional_edges("classifier_node", route_intent, handler_nodes)
    builder.add_conditional_edges("advance_intent_node", route_intent, handler_nodes)

    finish_map = {
        "advance_intent_node": "advance_intent_node",
        "post_process_node":   "post_process_node",
    }
    for node_name in handler_nodes:
        builder.add_conditional_edges(node_name, loop_or_finish, finish_map)

    builder.add_edge("post_process_node", END)

    memory = MemorySaver()
    return builder.compile(checkpointer=memory)


graph = _build_graph()

