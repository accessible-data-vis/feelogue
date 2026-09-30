"""
Chart loading and disambiguation.
"""
from .client import client
from .utils import parse_llm_json
from .config import OPENAI_MODEL
from .prompts import get_load_chart_prompt, get_load_chart_system_prompt
from .schema import LOAD_CHART_SCHEMA


def analyze_user_intent_with_context(user_query: str, state: dict) -> dict:
    """
    Resolve a load_chart request using chart_metadata_index.
    - If exactly one matching chart -> auto-load it.
    - If multiple (e.g., bar + line) -> ask user to choose.

    Returns a dict with keys:
        response, rtd_command, followup_stage, pending_chart_options
    The caller (load_chart_node) applies state patches.
    """
    
    metadata = state.get("chart_metadata_index",{})
    if isinstance(metadata, dict) and "charts" in metadata:
        charts = metadata.get("charts", [])
    elif isinstance(metadata, list):
        charts = metadata
    else:
        charts = []
    print("Available charts:", charts)
    if not charts:
        return {
            "response": f"I could not find any chart loaded. Please check your setup",
            "rtd_command": None,
            "followup_stage": False,
            "pending_chart_options": [],
        }
    messages = state.get("messages", [])
    # LLM as chart classifier
    resp = client.chat.completions.create( 
        model=OPENAI_MODEL,
        messages=[
            {"role": "system", "content": get_load_chart_system_prompt()},
            {"role": "user", "content": get_load_chart_prompt(charts=charts, query=user_query, messages=messages)},
        ],
        temperature=0,
        response_format={
            "type": "json_schema",
            "json_schema": {
                "name": "load_chart",
                "schema": LOAD_CHART_SCHEMA,
            },
        },
    )

    raw = (resp.choices[0].message.content or "").strip()
    result = parse_llm_json(raw, fallback={"matches": []})
    matches = result.get("matches", [])

    if not matches:
        chart_names = [ch.get("chart_name", ch.get("data_name", "unknown")) for ch in charts[:3]]
        return {
            "response": f"I couldn't find a chart matching that. Here are a few available charts: {', '.join(chart_names)}.",
            "rtd_command": None,
            "followup_stage": True,
            "pending_chart_options": [],
        }

    if len(matches) == 1:
        match = matches[0]
        ch = next((c for c in charts if c.get("chart_id") == match.get("chart_id")), None)
        if ch:
            return {
                "response": f"Loading the {match['chart_name']}.",
                "rtd_command": f"{ch.get('data_name')}-{ch.get('chart_type')}",
                "followup_stage": False,
                "pending_chart_options": [],
            }
        else:
            return {
            "response": f"I ran into a problem while loading {match.get("chart_name")}. Please try again",
            "rtd_command": None,
            "followup_stage": False,
            "pending_chart_options": [],
        }

    options = [m["chart_name"] for m in matches[:3]]
    pending = [
        ch for m in matches[:3]
        for ch in charts if ch.get("chart_id") == m.get("chart_id")
    ]
    return {
        "response": f"I found a few possible charts: {', '.join(options)}. Which would you like?",
        "rtd_command": None,
        "followup_stage": True,
        "pending_chart_options": pending,
    }

