"""Chart selection for load_chart. Consumed by agent/chart_loader.py."""
from ..utils import format_messages_to_str


def get_load_chart_system_prompt() -> str:
    """System prompt for choosing which chart a spoken request means."""
    return (
        "You are a chart selection assistant for Graphy, an assistive data visualisation system for blind users.\n\n"
        "The user query comes from a voice transcription, so it may contain typos, filler words, disfluencies, "
        "or inexact vocabulary. Account for this when matching — do not reject a query simply because a word "
        "is misspelled or phrased loosely.\n\n"
        "Because the user is blind, they cannot scan a list of results. "
        "Be precise: only return multiple charts when there is multiple charts that it can refer to "
        "If multiple charts are returned, the user will be interrupted and asked to clarify — "
        "this is disruptive, so try to avoid it.\n\n"
        "Given a list of available charts, identify which chart(s) the user wants to load:\n"
        "- If one chart is the best match — whether obvious or just better than the rest — return only that chart.\n"
        "- If two or more charts are genuinely ambiguous and you cannot confidently pick one, return all plausible matches ordered by relevance.\n"
        "- If nothing matches, return an empty list.\n\n"
        "You may also be given the currently loaded chart and recent conversation history. "
        "Use these to resolve relative or ambiguous references such as 'the previous one' or 'something similar'.\n\n"
        "Return JSON in the format: {\"matches\": [{\"chart_id\": <int>, \"chart_name\": <str>}, ...]}\n"
        "No explanation. No additional text."
    )


def get_load_chart_prompt(charts: list, query: str, messages:list[dict]) -> str:
    """User prompt: the available charts, recent history, and the request."""
    chart_lines = []
    for chart in charts:
        chart_id = chart.get("chart_id")
        chart_name = chart.get("chart_name", "Unknown")
        chart_type = chart.get("chart_type", "unknown")
        columns = chart.get("columns", [])
        columns_str = ", ".join(columns)
        chart_lines.append(
            f"- ID {chart_id}: \"{chart_name}\" ({chart_type}) | columns: [{columns_str}]"
        )

    compiled_chart_information = "\n".join(chart_lines)
    message_history = format_messages_to_str(messages)
    return f"""Available charts:
    {compiled_chart_information}

    User request: "{query}"

    Which chart(s) could the user be referring to? Return JSON with:
    - "matches": a list of objects, each with:
    - "chart_id": the integer ID of the chart
    - "chart_name": the name of the chart
    Order by relevance (best match first). Return an empty list if nothing matches.

    Here is some message history that might help to resolve the query:
    {message_history}

    Return ONLY the JSON object."""
