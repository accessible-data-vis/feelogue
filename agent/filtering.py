"""
Spoken series filtering: "filter everything apart from Storage", "hide GPU",
"show Memory again", "show all the series".

The model only reads the request into an action and the series it names; the
series are checked against the chart's real names and the new hidden set is
computed here. Unity receives the complete set to hide, so applying the same
command twice changes nothing.
"""
import json

from .client import client
from .config import OPENAI_MODEL
from .prompts import FILTER_EXTRACTION_SYSTEM_PROMPT, get_filter_extraction_prompt

ACTIONS = {"only", "hide", "show", "show_all"}


def _join(names: list[str]) -> str:
    return names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]


def match_series(requested: list[str], all_series: list[str]) -> tuple[list[str], list[str]]:
    """(matched chart names, names that match nothing), ignoring case."""
    by_lower = {s.lower(): s for s in all_series}
    matched, unknown = [], []
    for name in requested or []:
        hit = by_lower.get(str(name).strip().lower())
        if hit is None:
            unknown.append(str(name))
        elif hit not in matched:
            matched.append(hit)
    return matched, unknown


def resolve_filter(action: str, series: list[str], hidden: list[str], all_series: list[str],
                   force_send: bool = False):
    """New hidden set and the spoken confirmation. Returns (hidden or None, message);
    None means nothing is sent. With force_send an unchanged set is sent anyway."""
    hidden = [s for s in all_series if s in set(hidden or [])]
    if action == "show_all":
        new_hidden = []
    elif action == "only":
        new_hidden = [s for s in all_series if s not in series]
    elif action == "hide":
        new_hidden = [s for s in all_series if s in set(hidden) | set(series)]
    elif action == "show":
        new_hidden = [s for s in hidden if s not in series]
    else:
        return None, "I can show or hide whole series, for example: show only one series, or show all of them again."

    if len(new_hidden) == len(all_series):
        return None, "I can't hide every series; at least one has to stay on the chart."
    if new_hidden == hidden and not force_send:
        return None, _state_message(new_hidden, all_series, unchanged=True)
    return new_hidden, _state_message(new_hidden, all_series)


def _state_message(hidden: list[str], all_series: list[str], unchanged: bool = False) -> str:
    shown = [s for s in all_series if s not in hidden]
    if not hidden:
        text = "Showing all series"
    elif len(shown) == 1:
        text = f"Showing only {shown[0]}"
    else:
        text = f"Showing {_join(shown)}, with {_join(hidden)} hidden"
    return f"{text}, as already on the chart." if unchanged else f"{text}."


def extract_filter_request(query: str, all_series: list[str], hidden: list[str]) -> dict:
    """The model's reading of the request: {"action": ..., "series": [...]}."""
    resp = client.chat.completions.create(
        model=OPENAI_MODEL,
        temperature=0,
        response_format={"type": "json_object"},
        messages=[
            {"role": "system", "content": FILTER_EXTRACTION_SYSTEM_PROMPT},
            {"role": "user", "content": get_filter_extraction_prompt(query, all_series, hidden)},
        ],
    )
    out = json.loads(resp.choices[0].message.content or "{}")
    action = out.get("action")
    return {"action": action if action in ACTIONS else None,
            "series": [s for s in (out.get("series") or []) if isinstance(s, str)]}


def handle_filter_request(query: str, all_series: list[str], hidden: list[str], force_send: bool = False):
    """(rtd_command or None, new hidden list, spoken reply) for one spoken request."""
    if len(all_series) < 2:
        return None, hidden, "This chart has a single series, so there is nothing to filter."
    request = extract_filter_request(query, all_series, hidden)
    series, unknown = match_series(request["series"], all_series)
    if unknown:
        return None, hidden, (f"I couldn't find {_join(unknown)} on this chart. "
                              f"Its series are {_join(all_series)}.")
    if request["action"] in {"only", "hide", "show"} and not series:
        return None, hidden, f"Which series? This chart has {_join(all_series)}."
    new_hidden, message = resolve_filter(request["action"], series, hidden, all_series, force_send)
    if new_hidden is None:
        return None, hidden, message
    return {"filter": {"hidden_series": new_hidden}}, new_hidden, message
