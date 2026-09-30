"""Spoken series filtering. Consumed by agent/filtering.py."""
import json


# =============================================================================
# Series filtering
# =============================================================================

FILTER_EXTRACTION_SYSTEM_PROMPT = """You read a request to show or hide data series on a chart. Return JSON only: {"action": ..., "series": [...]}.
Actions:
- "only": keep just the named series, hide the rest ("filter everything apart from Storage", "only show Memory").
- "hide": hide the named series ("hide GPU", "remove Memory and Storage").
- "show": bring hidden series back ("show GPU again", "put Memory back").
- "show_all": show every series ("show all the series", "clear the filter", "undo the filter").
"series" lists the series the user names, copied exactly from the chart's series list; [] for show_all. If the request names something not in the list, copy the user's word as given."""


def get_filter_extraction_prompt(query: str, all_series: list[str], hidden: list[str]) -> str:
    """User prompt: the chart's series, what's hidden, and the request."""
    return (f"Chart series: {json.dumps(all_series)}\n"
            f"Currently hidden: {json.dumps(hidden)}\n"
            f"Request: {json.dumps(query)}")
