"""
Chart loading and disambiguation.
"""
from difflib import SequenceMatcher
from .utils import _norm
from .client import client
from .utils import parse_llm_json
from .config import OPENAI_MODEL
from .prompts import get_load_chart_prompt, get_load_chart_system_prompt
from .schema import LOAD_CHART_SCHEMA


# Old intent matching function
# def analyze_user_intent_with_context(user_query: str, context: dict) -> dict:
#     """
#     Resolve a load_chart request using chart_metadata_index.
#     - If exactly one matching chart -> auto-load it.
#     - If multiple (e.g., bar + line) -> ask user to choose.

#     Returns a dict with keys:
#         response, rtd_command, followup_stage, pending_chart_options
#     No longer mutates context -- the caller (load_chart_node) applies state patches.
#     """
#     metadata = context.get("chart_metadata_index") or {}
#     if isinstance(metadata, dict) and "charts" in metadata:
#         charts = metadata.get("charts", [])
#     elif isinstance(metadata, list):
#         charts = metadata
#     else:
#         charts = []
#     print("Available charts:", charts)
#     def _norm_text(s: str) -> str:
#         return _norm(s)

#     q_lower = user_query.lower()
#     requested_chart_type = None
#     for ct in ["line", "bar", "scatter", "map"]:
#         if ct in q_lower:
#             requested_chart_type = ct
#             break

#     stop = {
#         "load", "show", "display", "plot", "chart", "charts",
#         "line", "bar", "scatter", "scatterplot", "data", "the", "a", "an",
#         "stock", "graph", "diagram", "map"
#     }
#     q_tokens = [t for t in _norm_text(user_query).split() if t and t not in stop]

#     candidates = []
#     if charts and q_tokens:
#         for ch in charts:
#             if requested_chart_type:
#                 ch_type = ch.get("chart_type", "").lower()
#                 if ch_type != requested_chart_type:
#                     continue

#             blob = f"{ch.get('data_name', '')} {ch.get('chart_name', '')}"
#             blob_norm = _norm_text(blob)
#             blob_tokens = blob_norm.split()

#             overlap = len(set(q_tokens) & set(blob_tokens))
#             seq_ratio = SequenceMatcher(None, " ".join(q_tokens), blob_norm).ratio()
#             score = overlap + seq_ratio

#             if overlap > 0 and score > 0.3:
#                 candidates.append((score, ch))

#     candidates.sort(key=lambda x: x[0], reverse=True)

#     if len(candidates) >= 2:
#         top_score = candidates[0][0]
#         second_score = candidates[1][0]
#         if top_score >= second_score * 1.5:
#             candidates = [candidates[0]]

#     # Handle follow-up disambiguation response
#     if context.get("followup_stage") and context.get("followup_topic") == "load_chart":
#         pending = context.get("pending_chart_options", [])
#         q_lower = user_query.lower().strip()

#         for ch in pending:
#             chart_type = ch.get("chart_type", "").lower()
#             if chart_type and chart_type in q_lower:
#                 return {
#                     "response": f"Loading the {ch.get('chart_name', 'chart')}.",
#                     "rtd_command": f"{ch.get('data_name')}-{ch.get('chart_type')}",
#                     "followup_stage": False,
#                     "pending_chart_options": [],
#                 }

#         number_map = {"1": 0, "2": 1, "3": 2, "first": 0, "second": 1, "third": 2, "one": 0, "two": 1}
#         for word, idx in number_map.items():
#             if word in q_lower and idx < len(pending):
#                 ch = pending[idx]
#                 return {
#                     "response": f"Loading the {ch.get('chart_name', 'chart')}.",
#                     "rtd_command": f"{ch.get('data_name')}-{ch.get('chart_type')}",
#                     "followup_stage": False,
#                     "pending_chart_options": [],
#                 }

#     if not candidates:
#         if charts:
#             chart_names = [ch.get("chart_name", ch.get("data_name", "unknown")) for ch in charts[:5]]
#             return {
#                 "response": f"I couldn't find a chart matching that. Available charts include: {', '.join(chart_names)}. Which would you like?",
#                 "rtd_command": None,
#                 "followup_stage": True,
#                 "pending_chart_options": [],
#             }
#         return {
#             "response": "I don't have any charts available to load.",
#             "rtd_command": None,
#             "followup_stage": False,
#             "pending_chart_options": [],
#         }

#     if len(candidates) == 1:
#         ch = candidates[0][1]
#         return {
#             "response": f"Loading the {ch.get('chart_name', 'chart')}.",
#             "rtd_command": f"{ch.get('data_name')}-{ch.get('chart_type')}",
#             "followup_stage": False,
#             "pending_chart_options": [],
#         }

#     top_data_name = candidates[0][1].get("data_name")
#     same_data = [c for c in candidates if c[1].get("data_name") == top_data_name]

#     if len(same_data) > 1 and len(same_data) == len(candidates):
#         chart_types = [c[1].get("chart_type", "chart") for c in same_data]
#         return {
#             "response": f"I found multiple chart types for that data: {', '.join(chart_types)}. Which would you prefer?",
#             "rtd_command": None,
#             "followup_stage": True,
#             "pending_chart_options": [c[1] for c in same_data],
#         }

#     options = [c[1].get("chart_name", c[1].get("data_name")) for c in candidates[:3]]
#     return {
#         "response": f"I found multiple options: {', '.join(options)}. Which would you like?",
#         "rtd_command": None,
#         "followup_stage": True,
#         "pending_chart_options": [c[1] for c in candidates[:3]],
#     }



def analyze_user_intent_with_context(user_query: str, state: dict) -> dict:
    """
    Resolve a load_chart request using chart_metadata_index.
    - If exactly one matching chart -> auto-load it.
    - If multiple (e.g., bar + line) -> ask user to choose.

    Returns a dict with keys:
        response, rtd_command, followup_stage, pending_chart_options
    No longer mutates context -- the caller (load_chart_node) applies state patches.
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
            {"role": "user", "content": get_load_chart_prompt(charts=charts, query=user_query, messages=messages[-6:])},
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
            "response": f"I couldn't find a chart matching that. Here is a few available charts: {', '.join(chart_names)}.",
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

