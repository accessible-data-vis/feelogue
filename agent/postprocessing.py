"""
Post-processing functions for agent responses.
"""
import re

import pandas as pd

from .client import client
from .config import OPENAI_MODEL
from .prompts import get_highlight_extraction_prompt, get_rewrite_list_prompt, get_combine_multi_intent_responses_prompt
from .utils import _extract_bulleted_items, rewrite_long_lists_locally, parse_llm_json


def combine_multi_intent_responses(responses: dict[str, str]) -> str:
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
            messages=[{"role": "user", "content": get_combine_multi_intent_responses_prompt(responses)}],
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
            temperature=0.2,
        )
        rewritten = (resp.choices[0].message.content or "").strip()
        if rewritten:
            return rewritten
    except Exception:
        pass
    return rewrite_long_lists_locally(text, max_per_sentence=2, min_trigger=4)


# =============================================================================
# Old regex-based extraction (kept for reference)
# =============================================================================
# def extract_numerical_entities(text: str) -> dict | None:
#     """
#     Extract numerical entities (years, numbers) from response text
#     for highlighting on the RTD.
#     """
#     if not text:
#         return None
#     years = [(m.group(0), m.start()) for m in re.finditer(r"\b((?:19|20)\d{2})\b", text)]
#     nums = [(m.group(1), m.start()) for m in re.finditer(r"([-+]?\d+(?:\.\d+)?)", text)]
#     nodes = {}
#     for i, (v, _) in enumerate(nums, 1):
#         try:
#             nodes[f"node_{i}"] = {"value": float(v)}
#         except Exception:
#             pass
#     for j, (y, _) in enumerate(years, len(nodes) + 1):
#         try:
#             nodes[f"node_{j}"] = {"year": int(y)}
#         except Exception:
#             pass
#     return nodes or None


# =============================================================================
# LLM-based extraction (matches response to actual data points)
# =============================================================================


def extract_highlighted_data_points(
    response_text: str,
    df: pd.DataFrame,
    x_col: str,
    y_col: str,
    color_col: str | None = None,
) -> dict | None:
    """
    Ask LLM which data points from the DataFrame it referenced in its response,
    then return those as highlight nodes for the RTD.

    Returns:
        dict of nodes with actual x/y values, or None if no highlights found.
        Example: {'node_1': {'x': '2024/Q1', 'y': 4.35}}
    """
    if not response_text or df is None or df.empty or not x_col or not y_col:
        return None

    # Get list of valid x-values from the data (unique to avoid duplicate hints to LLM)
    x_values = df[x_col].astype(str).unique().tolist()

    # For series-aware extraction, also pass series values
    series_values = None
    if color_col and color_col in df.columns:
        series_values = df[color_col].astype(str).unique().tolist()

    # Ask LLM which data points it referenced
    prompt = get_highlight_extraction_prompt(response_text, x_values, color_col=color_col, series_values=series_values)

    try:
        resp = client.chat.completions.create(
            model=OPENAI_MODEL,
            messages=[{"role": "user", "content": prompt}],
            temperature=0,
        )
        raw = (resp.choices[0].message.content or "").strip()

        highlights = parse_llm_json(raw, fallback=[])

        if not isinstance(highlights, list) or not highlights:
            return None

        # Match highlights to actual data points
        nodes = {}
        node_idx = 1

        for item in highlights:
            # Series-aware: item may be {"x": ..., "<color_col>": ...} or a plain string
            if color_col and isinstance(item, dict):
                x_val_ref = str(item.get("x", ""))
                series_ref = item.get(color_col)
                mask = df[x_col].astype(str) == x_val_ref

                if series_ref and color_col in df.columns:
                    # Match by both x and series
                    mask = mask & (df[color_col].astype(str) == str(series_ref))
                    matches = df[mask]
                    if not matches.empty:
                        row = matches.iloc[0]
                        node = _build_node(row, x_col, y_col)
                        node[color_col] = _to_native(row[color_col])
                        nodes[f"node_{node_idx}"] = node
                        node_idx += 1
                else:
                    # x-value only — return all series rows for that x
                    matches = df[mask]
                    for _, row in matches.iterrows():
                        node = _build_node(row, x_col, y_col)
                        if color_col in df.columns:
                            node[color_col] = _to_native(row[color_col])
                        nodes[f"node_{node_idx}"] = node
                        node_idx += 1
            else:
                # Legacy plain string (non-series path)
                x_val_ref = str(item)
                mask = df[x_col].astype(str) == x_val_ref
                matches = df[mask]

                if not matches.empty:
                    row = matches.iloc[0]
                    nodes[f"node_{node_idx}"] = _build_node(row, x_col, y_col)
                    node_idx += 1

        return nodes if nodes else None

    except Exception as e:
        print(f"Warning: Highlight extraction failed: {e}")
        return None


def _to_native(val):
    """Convert numpy scalar to native Python type."""
    return val.item() if hasattr(val, "item") else val


def _build_node(row, x_col: str, y_col: str) -> dict:
    """Build a highlight node dict from a DataFrame row."""
    return {
        "x": _to_native(row[x_col]),
        "y": _to_native(row[y_col]),
    }
