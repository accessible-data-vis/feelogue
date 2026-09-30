"""
Post-processing functions for agent responses.
"""
import re

from .client import client
from .config import OPENAI_MODEL
from .prompts import get_chunk_assignment_prompt, get_rewrite_list_prompt, get_combine_multi_intent_responses_prompt
from .schema import CHUNK_ASSIGNMENT_SCHEMA
from .utils import _extract_bulleted_items, rewrite_long_lists_locally, parse_llm_json


def combine_multi_intent_responses(responses: dict[str, str], query: str) -> str:
    """
    Combine multiple response fragments into one coherent answer using GPT.
    """
    if not responses:
        return ""
    if len(responses) == 1:
        _, value = next(iter(responses.items()))
        return value

    try:
        resp = client.chat.completions.create(
            model=OPENAI_MODEL,
            messages=[{"role": "user", "content": get_combine_multi_intent_responses_prompt(responses=responses, query=query)}],
            temperature=0,
        )
        combined = (resp.choices[0].message.content or "").strip()
        if combined:
            return combined
    except Exception as e:
        print(f"Warning: Response combination failed: {e}")

    # Simple fallback: join with spaces
    return " ".join(responses.values())


def rewrite_long_node_lists_with_gpt(text: str) -> str:
    """
    Rewrite long bulleted lists in the response into concise sentences.
    Falls back to local rewriting if GPT fails.
    """
    _, items, _, _ = _extract_bulleted_items(text)
    if len(items) < 4:
        return text
    try:
        resp = client.chat.completions.create(
            model=OPENAI_MODEL,
            messages=[{"role": "user", "content": get_rewrite_list_prompt(text)}],
            temperature=0,
        )
        rewritten = (resp.choices[0].message.content or "").strip()
        if rewritten:
            return rewritten
    except Exception:
        pass
    return rewrite_long_lists_locally(text, max_per_sentence=2, min_trigger=4)


# =============================================================================
# Sentence chunking
# =============================================================================
# The only place text is split: Unity plays these chunks verbatim and highlight
# nodes refer to them by index.

# Pass 1: split before "N. Capital" when preceded by sentence-ending punctuation or colon
_LIST_ITEM_RE = re.compile(r"(?<=[.:?!])\s+(?=\d+\.\s+[A-Z])")

# Pass 2: split on sentence boundaries within a chunk (leading list number already
# stripped). Python lookbehind must be fixed-width, hence (?<=\d\.).
_SENTENCE_RE = re.compile(r"(?<=[a-zA-Z%][.?!])\s+(?=[A-Z(])|(?<=\d\.)\s+(?=[A-Z(])")

# Detects a leading list number at the start of a chunk, e.g. "6. "
_LEADING_LIST_NUMBER_RE = re.compile(r"^\d+\.\s+")


def split_into_chunks(text: str) -> list[str]:
    """
    Split response text into spoken chunks: numbered list items first,
    then sentences within each item.
    """
    text = (text or "").strip()
    if not text:
        return []

    pass1 = [p.strip() for p in _LIST_ITEM_RE.split(text) if p.strip()]

    result = []
    for part in pass1:
        m = _LEADING_LIST_NUMBER_RE.match(part)
        prefix = m.group(0) if m else ""
        body = part[len(prefix):]

        sentences = [s.strip() for s in _SENTENCE_RE.split(body) if s.strip()]
        if not sentences:
            result.append(part)
        else:
            result.append(prefix + sentences[0])
            result.extend(sentences[1:])
    return result


# =============================================================================
# Highlights: the answer names its rows; each is placed on a spoken sentence
# =============================================================================

_FALLBACK_DATA_QUERY_RESPONSE = {
    "message": "Sorry, something went wrong looking into that. Could you ask again?",
    "highlighted_ids": [],
}


def parse_data_query_response(raw: str) -> dict:
    """Parse the data-query loop's schema-conforming answer into
    {"message": str, "highlighted_ids": list[str]}."""
    return parse_llm_json(raw, fallback=dict(_FALLBACK_DATA_QUERY_RESPONSE))


def resolve_highlighted_nodes(highlighted_ids: list, df, x_col: str, y_col: str,
                              color_col: str | None = None) -> dict:
    """Resolve the answer's `_id`s to wire nodes, keyed by id. Pure (no LLM call);
    unknown ids are dropped, so the wire only carries resolvable pins."""
    if not highlighted_ids or df is None or df.empty or "_id" not in df.columns:
        return {}
    id_str = df["_id"].astype(str)
    resolved = {}
    for hid in highlighted_ids:
        matches = df[id_str == str(hid)]
        if not matches.empty:
            resolved[str(hid)] = node_from_row(matches.iloc[0], df, x_col, y_col, color_col)
    return resolved


def node_from_row(row, df, x_col: str, y_col: str, color_col: str | None) -> dict:
    """Wire node from a df row: id + x/y (+ series)."""
    node = {}
    if "_id" in df.columns:
        node["id"] = str(row["_id"])
    node.update(_build_node(row, x_col, y_col))
    if color_col and color_col in df.columns:
        node[color_col] = _to_native(row[color_col])
    return node


def assign_nodes_to_chunks(nodes: dict, chunks: list[str]) -> dict:
    """Merge a "chunk" index onto each node: the spoken sentence that references it.
    Unassigned or invalid entries are left without one (they blink throughout)."""
    if not nodes or not chunks:
        return nodes
    try:
        resp = client.chat.completions.create(
            model=OPENAI_MODEL,
            messages=[{"role": "user", "content": get_chunk_assignment_prompt(chunks, nodes)}],
            temperature=0,
            response_format={
                "type": "json_schema",
                "json_schema": {"name": "chunk_assignment", "schema": CHUNK_ASSIGNMENT_SCHEMA},
            },
        )
        raw = (resp.choices[0].message.content or "").strip()
        assignments = parse_llm_json(raw, fallback={"assignments": []}).get("assignments") or []
    except Exception as e:
        print(f"Warning: Chunk assignment failed: {e}")
        return nodes

    result = {nid: dict(n) for nid, n in nodes.items()}
    for item in assignments:
        if not isinstance(item, dict):
            continue
        nid, idx = item.get("id"), item.get("chunk")
        if nid in result and isinstance(idx, int) and 0 <= idx < len(chunks):
            result[nid]["chunk"] = idx
    return result


def _to_native(val):
    """Convert numpy scalar to native Python type."""
    return val.item() if hasattr(val, "item") else val


def _build_node(row, x_col: str, y_col: str) -> dict:
    """Build a highlight node dict from a DataFrame row."""
    return {
        "x": _to_native(row[x_col]),
        "y": _to_native(row[y_col]),
    }

