"""
LangGraph graph for the Feelogue Agent.
Replaces the old orchestrator + minimal graph with a full StateGraph
where every LLM call has access to persistent conversation history.
"""
import json

from langchain_core.messages import HumanMessage, AIMessage, SystemMessage, ToolMessage
from langchain_openai import ChatOpenAI
from langgraph.graph import StateGraph, END
from langgraph.checkpoint.memory import MemorySaver

from .config import OPENAI_MODEL, OPENAI_MODEL_ANALYSIS, OPENAI_MODEL_IMAGE
from .client import client
from .state import AgentState
from .data_query import csv_query_tool
from .intent import classify_query
from .prompts import (
    get_data_query_system_prompt,
    get_chart_overview_prompt,
    IMAGE_ANALYSIS_SYSTEM_PROMPT,
    CHART_OVERVIEW_SYSTEM_PROMPT,
)
from .chart_loader import analyze_user_intent_with_context
from .operations import (
    build_operation_ack,
    build_operations_rtd_command,
    resolve_operation_targets_to_values,
)
from .postprocessing import (
    extract_highlighted_data_points,
    rewrite_long_node_lists_with_gpt,
    combine_multi_intent_responses,
)
from .touch_context import collect_touch_nodes, collect_highlight_nodes, _pick_best_node_values
from .utils import strip_markdown
from .context import get_df


# =============================================================================
# LLM + tool setup
# =============================================================================

_main_llm = ChatOpenAI(model=OPENAI_MODEL_ANALYSIS, temperature=0)
_tools = [csv_query_tool]
_tools_by_name = {t.name: t for t in _tools}
_llm_with_tools = _main_llm.bind_tools(_tools)
_max_iter : int = 6 # number of iteration that data query is allowed to run

def _run_tool_loop(state: AgentState , enriched_query:str, max_iterations: int = 6) -> str:
    """Synchronous tool-calling loop. Returns the final text response.

    `state` is merged into each tool call's args so tools annotated with
    InjectedState receive it -- that annotation is only auto-filled by
    LangGraph's ToolNode, which this hand-rolled loop doesn't use.
    """
    import pandas as pd
    

    df = get_df()
    state["current_query"] = enriched_query

    # Build df_context for system prompt
    df_cols = state.get("df_columns") or []
    head = df.head(6).to_dict(orient="records") if isinstance(df, pd.DataFrame) else []
    tail = df.tail(6).to_dict(orient="records") if isinstance(df, pd.DataFrame) else []
    df_context = {
        "columns": df_cols,
        "x_field": state.get("x_field"),
        "y_field": state.get("y_field"),
        "head": head,
        "tail": tail,
    }

    iters_left = max_iterations
    # Build message list: system prompt (placeholder, filled in each loop iteration) + prior conversation + new query
    msgs_for_llm = [SystemMessage(content="")] + list(state.get("messages", [])) + [HumanMessage(content=enriched_query)]
    for _ in range(max_iterations):
        # Rebuild the system prompt each iteration so the LLM knows how many iterations remain
        msgs_for_llm[0] = SystemMessage(content=get_data_query_system_prompt(
            json.dumps(df_context),
            iters_left,
            data_name=state.get("data_name") or state.get("active_layer") or "the current dataset",
            x_field=state.get("x_field") or "x-axis",
            y_field=state.get("y_field") or "y-axis",
            df=df,
            vega_lite_schema=state.get("vega_lite_schema")
            
        ))
        response = _llm_with_tools.invoke(msgs_for_llm)
        msgs_for_llm.append(response)
        if not response.tool_calls:
            return response.content or ""
        for tc in response.tool_calls:
            result = _tools_by_name[tc["name"]].invoke({**tc["args"], "state": state})
            msgs_for_llm.append(ToolMessage(content=str(result), tool_call_id=tc["id"]))
        iters_left -= 1
    return msgs_for_llm[-1].content or ""


# =============================================================================
# Node: input_node
# =============================================================================

def input_node(state: AgentState) -> dict:
    """Entry point of langgraph"""
    print(f"[input_node],message length: {len(state.get("messages"))}")
    return {
        "rtd_command": None,
        "nodes": {},
        "touch_used": False,
        "highlight_used": False,
        "touch_nodes": {},
        "highlight_nodes": {},
        "final_response": "",
    }


# =============================================================================
# Node: classifier_node
# =============================================================================

def classifier_node(state: AgentState) -> dict:
    """Classify intents and detect deictic references with conversation history."""
    user_query = state.get("user_query", "")
    messages = state.get("messages", [])
    result = classify_query(
        user_query,
        has_image=bool(state.get("image_data")),
        messages=messages,
    )
    intents = result["intents"]
    has_deictic = result["has_deictic"]

    print(f"[classifier_node] {[i['type'] for i in intents]}")

    first = intents[0] if intents else {"type": "general_question", "query": user_query}
    return {
        "intents": intents,
        "has_deictic": has_deictic,
        "intent_index": 0,
        "current_intent": first["type"],
        "current_query": first["query"],
        "intent_responses": None,   # None triggers _merge_dict_or_reset to clear to {}
    }


# =============================================================================
# Routing helpers (conditional edge functions)
# =============================================================================

_INTENT_NODE_MAP = {
    "load_chart":     "load_chart_node",
    "image_analysis": "image_analysis_node",
    "operations":     "operations_node",
    "chart_overview": "chart_overview_node",
}


def route_intent(state: AgentState) -> str:
    intent = state.get("current_intent", "general_question")
    destination = _INTENT_NODE_MAP.get(intent, "data_query_node")
    print(f"[route_intent] -> {destination}")
    return destination


def loop_or_finish(state: AgentState) -> str:
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
    return {
        "intent_responses": {state["current_intent"]: analysis["response"]},
        "rtd_command": analysis.get("rtd_command"),
        "followup_stage": followup,
        "followup_topic": "load_chart" if followup else None,
        "pending_chart_options": analysis.get("pending_chart_options", []),
    }


# =============================================================================
# Node: image_analysis_node
# =============================================================================

def image_analysis_node(state: AgentState) -> dict:
    """Analyze the current chart image using multimodal LLM."""
    base64_image = state.get("image_data")
    image_format = state.get("image_format") or "png"
    print(f"[image_analysis_node]")

    if not base64_image:
        return {
            "intent_responses": {state["current_intent"]: "I don't have an image of the current chart to analyze."},
            "followup_stage": False,
        }

    response = client.chat.completions.create(
        model=OPENAI_MODEL_IMAGE,
        messages=[
            {"role": "system", "content": IMAGE_ANALYSIS_SYSTEM_PROMPT},
            {
                "role": "user",
                "content": [
                    {"type": "text", "text": state.get("current_query", "")},
                    {
                        "type": "image_url",
                        "image_url": {"url": f"data:image/{image_format};base64,{base64_image}"},
                    },
                ],
            },
        ],
        temperature=0,
    )
    result_text = response.choices[0].message.content
    return {
        "intent_responses": {state["current_intent"]: result_text},
        "followup_stage": False,

    }


# =============================================================================
# Node: operations_node
# =============================================================================

def operations_node(state: AgentState) -> dict:
    """Extract and resolve a chart operation (zoom, pan, layer switch)."""
    print(f"[operations_node]")
    from .context import get_df
    df = get_df()
    x_col = state.get("x_field")
    y_col = state.get("y_field")

    x_values = None
    if df is not None and x_col and x_col in df.columns:
        x_values = df[x_col].astype(str).tolist()

    rtd_cmd = build_operations_rtd_command(
        user_query=state.get("current_query", ""),
        touch_context=state.get("touchdata", {}),
        highlighted_context=state.get("highlighted_context", {}),
        x_values=x_values,
    )
    rtd_cmd = resolve_operation_targets_to_values(
        user_query=state.get("current_query", ""),
        rtd_cmd=rtd_cmd,
        df=df,
        x_col=x_col,
        y_col=y_col,
    )
    ack = build_operation_ack(rtd_cmd)
    return {
        "intent_responses": {state["current_intent"]: ack},
        "rtd_command": rtd_cmd,
        "followup_stage": False,

    }


# =============================================================================
# Node: chart_overview_node
# =============================================================================

def chart_overview_node(state: AgentState) -> dict:
    """Generate a spoken overview of the current chart."""
    print(f"[chart_overview_node]")
    pre_built = state.get("chart_overview")

    if pre_built:
        if isinstance(pre_built, dict):
            parts = []
            if pre_built.get("title"):
                parts.append(pre_built["title"] + ".")
            if pre_built.get("description"):
                parts.append(pre_built["description"])
            for series_info in pre_built.get("series", []):
                if isinstance(series_info, dict):
                    name = series_info.get("name", "")
                    desc = series_info.get("description", "")
                    if name and desc:
                        parts.append(f"{name}: {desc}")
                    elif desc:
                        parts.append(desc)
                elif isinstance(series_info, str):
                    parts.append(series_info)
            response = " ".join(parts) if parts else str(pre_built)
        else:
            response = str(pre_built)
        return {"intent_responses": {state["current_intent"]: response}, "followup_stage": False}

    chart_type = state.get("chart_type")
    df_cols = state.get("df_columns") or []

    if not chart_type or not df_cols:
        return {
            "intent_responses": {state["current_intent"]: "I don't have a chart loaded yet. Please load a chart first."},
            "followup_stage": False,
        }

    x_col = state.get("x_field") or df_cols[0]
    y_col = state.get("y_field") or (df_cols[1] if len(df_cols) >= 2 else "y-axis")

    try:
        resp = client.chat.completions.create(
            model=OPENAI_MODEL,
            messages=[
                {"role": "system", "content": CHART_OVERVIEW_SYSTEM_PROMPT},
                {"role": "user", "content": get_chart_overview_prompt(
                    x_col, y_col, chart_type, color_col=state.get("color_field")
                )},
            ],
            temperature=0,
        )
        response = resp.choices[0].message.content.strip()
    except Exception as e:
        print(f"Warning: GPT overview fallback due to: {e}")
        response = f"This {chart_type} chart shows how {y_col} changes with respect to {x_col}."

    return {"intent_responses": {state["current_intent"]: response}, "followup_stage": False}


# =============================================================================
# Node: data_query_node
# =============================================================================

def data_query_node(state: AgentState) -> dict:
    """
    Handle data_analysis, trend, touch_interaction, and general_question intents.
    Enriches the query with touch/highlight context, runs the inline tool loop,
    then post-processes the response.
    """
    query = state.get("current_query", "")
    print(f"[data_query_node] intent={state.get('current_intent')!r}")
    touchdata = state.get("touchdata", {})
    highlighted_context = state.get("highlighted_context", {})

    # Touch & highlight referent enrichment
    touch_info, touch_nodes = collect_touch_nodes(touchdata)
    highlight_info, highlight_nodes = collect_highlight_nodes(highlighted_context)
    use_touch = len(touch_nodes) > 0
    use_highlight = len(highlight_nodes) > 0

    referent_parts = []
    if use_touch:
        referent_parts.extend(touch_info)
    if use_highlight:
        referent_parts.extend(highlight_info)

    enriched_query = f"{query} ({'; '.join(referent_parts)})" if referent_parts else query

    # Persist best referent for future deictic ops
    patch_referents = {}
    if use_touch:
        best_nv = _pick_best_node_values(touch_nodes)
        if best_nv:
            patch_referents["last_touch_node_values"] = best_nv
            patch_referents["last_referent_node_values"] = best_nv
    elif use_highlight:
        best_nv = _pick_best_node_values(highlight_nodes)
        if best_nv:
            patch_referents["last_referent_node_values"] = best_nv

    response_text = _run_tool_loop(state=state, enriched_query=enriched_query)

    print(f"response from tool loop: \n {response_text}")
    # Post-processing
    response_text = strip_markdown(response_text)
    rewritten = rewrite_long_node_lists_with_gpt(response_text)
    if rewritten != response_text:
        response_text = rewritten

    extracted_nodes = extract_highlighted_data_points(
        response_text=response_text,
        df=get_df(),
        x_col=state.get("x_field"), 
        y_col=state.get("y_field"),
        color_col=state.get("color_field"),
    )
    print(f"[data_query_node] response={response_text!r}")

    return {
        "intent_responses": {state["current_intent"]: response_text},
        "nodes": extracted_nodes or {},
        "touch_used": use_touch,
        "highlight_used": use_highlight,
        "touch_nodes": touch_nodes if use_touch else {},
        "highlight_nodes": highlight_nodes if use_highlight else {},
        "followup_stage": False,
        **patch_referents,
    }


# =============================================================================
# Node: post_process_node
# =============================================================================

def post_process_node(state: AgentState) -> dict:
    """Assemble the final response from all per-intent results."""
    intent_responses = state.get("intent_responses") or {}
    print(f"[post_process_node]")

    if len(intent_responses) > 1:
        final_response = combine_multi_intent_responses(responses=intent_responses,query=state.get("user_query"))
    elif len(intent_responses) == 1:
        final_response = next(iter(intent_responses.values()))
    else:
        final_response = "I'm not sure how to help with that."
    return {
        "final_response": final_response, 
        "messages": [
        HumanMessage(
            content=state.get("current_query", ""),
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
    builder.add_node("image_analysis_node", image_analysis_node)
    builder.add_node("operations_node", operations_node)
    builder.add_node("chart_overview_node", chart_overview_node)
    builder.add_node("data_query_node", data_query_node)
    builder.add_node("post_process_node", post_process_node)

    builder.set_entry_point("input_node")
    builder.add_edge("input_node", "classifier_node")

    handler_nodes = {
        "load_chart_node":     "load_chart_node",
        "image_analysis_node": "image_analysis_node",
        "operations_node":     "operations_node",
        "chart_overview_node": "chart_overview_node",
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
    return builder.compile(checkpointer=memory), memory


graph, _memory = _build_graph()


def clear_graph_thread(thread_id: str) -> None:
    """Remove all checkpoint data for a thread from MemorySaver."""
    to_delete = [
        k for k in _memory.storage
        if isinstance(k, tuple) and len(k) > 0 and k[0] == thread_id
    ]
    for k in to_delete:
        del _memory.storage[k]
    if to_delete:
        print(f"Cleared {len(to_delete)} checkpoint(s) for graph thread '{thread_id}'")
