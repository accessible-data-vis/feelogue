"""
Utility functions for text normalization and parsing.
"""
import json
import re
import string

def _split_camel_and_snake(s: str) -> str:
    """Split camelCase and snake_case into spaces."""
    s = re.sub(r"([a-z])([A-Z])", r"\1 \2", s)
    s = s.replace("_", " ")
    return s


def _norm(text: str) -> str:
    """Normalize text for matching: lowercase, remove punctuation, collapse whitespace."""
    text = text or ""
    text = _split_camel_and_snake(text).lower()
    text = re.sub(r"[\(\[].*?[\)\]]", " ", text)
    text = text.translate(str.maketrans("", "", string.punctuation))
    return re.sub(r"\s+", " ", text).strip()


def _extract_bulleted_items(text: str):
    """Extract bulleted list items from text."""
    lines = text.splitlines()
    bullet = re.compile(r"^\s*[-*]\s+(.+?)\s*$")
    items, idxs = [], []
    for i, ln in enumerate(lines):
        m = bullet.match(ln)
        if m:
            items.append(m.group(1).strip())
            idxs.append(i)
    if not items:
        return text, [], -1, -1
    return "\n".join(lines[:idxs[0]]).rstrip(), items, idxs[0], idxs[-1]


def parse_llm_json(raw: str, fallback: dict) -> dict:
    """
    Parse a JSON response from an LLM, handling markdown code fences.
    Returns fallback dict if parsing fails, or if the JSON is not an object:
    a bare number or list is valid JSON but not an answer, and every caller
    reads the result with .get().
    """
    raw = (raw or "").strip()
    if raw.startswith("```"):
        raw = re.sub(r"^```(?:json)?\n?", "", raw)
        raw = re.sub(r"\n?```$", "", raw)
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError:
        return fallback
    return parsed if isinstance(parsed, dict) else fallback


def strip_markdown(text: str) -> str:
    """Remove markdown formatting so TTS reads clean text."""
    # Bold and italic
    text = re.sub(r"\*{1,3}(.+?)\*{1,3}", r"\1", text)
    # Headings
    text = re.sub(r"^#{1,6}\s+", "", text, flags=re.MULTILINE)
    # Inline code
    text = re.sub(r"`(.+?)`", r"\1", text)
    # Collapse all newlines to spaces for TTS
    text = re.sub(r"\n+", " ", text)
    text = re.sub(r" {2,}", " ", text)
    return text.strip()


def rewrite_long_lists_locally(text: str, max_per_sentence: int = 2, min_trigger: int = 4) -> str:
    """Rewrite long bulleted lists into prose for TTS."""
    prefix, items, s, e = _extract_bulleted_items(text)
    if len(items) < min_trigger:
        return text

    chunks = []
    for i in range(0, len(items), max_per_sentence):
        chunk = items[i:i + max_per_sentence]
        chunks.append(", ".join(chunk))

    prose = ". ".join(chunks) + "."
    return f"{prefix} {prose}" if prefix else prose

def format_messages_to_str(messages: list) -> str:
    """Conversation history as prompt text; long assistant turns are cut to 600 characters."""
    lines = []
    message_count = 0
    for msg in messages:
        role = getattr(msg, "type", None)
        content = getattr(msg, "content", "")
        metadata = getattr(msg, "metadata", None) or {}

        if role == "human":
            intents = metadata.get("intent", [])
            intent_str = f" [{', '.join(intents)}]" if intents else ""
            lines.append(f"User{intent_str}: {content}")
            message_count += 1

        elif role == "ai":
            if len(content) > 600:
                content = content[:600] + "..."
            lines.append(f"Assistant: {content}")
            message_count += 1

    if not lines:
        return ""

    return (
        f"\nCONVERSATION HISTORY ({message_count} messages):\n"
        + "\n".join(lines)
        + "\n"
    )

def trim_schema_data(schema : dict, n : int=5) -> dict:
    """
    Keep only the first n and last n rows in schema["data"]["values"].
    Annotates rows as head/tail and inserts a marker showing omitted rows.

    Args:
        schema (dict): schema containing data.values
        n (int): number of rows to keep from head and tail

    Returns:
        dict: modified schema
    """

    from copy import deepcopy

    if not schema:
        return schema or {}
    # Avoid changing original schema
    trimmed_schema = deepcopy(schema)
    for key in ("image_data", "image_format", "overview", "metadata"):
        trimmed_schema.pop(key, None)
    trimmed_schema.setdefault("data", {})

    values = trimmed_schema["data"].get("values", [])

    # Small datasets: keep everything
    if len(values) <= 2 * n:
        trimmed_schema["data"]["values"] = [
            {"_sample_position": "all", **row}
            for row in values
        ]
        return trimmed_schema

    head = [
        {"_sample_position": "head", **row}
        for row in values[:n]
    ]

    tail = [
        {"_sample_position": "tail", **row}
        for row in values[-n:]
    ]

    removed = [{
        "_sample_position": "removed",
        "message": f"{len(values) - (2*n)} rows removed"
    }]

    trimmed_schema["data"]["values"] = head + removed + tail
    return trimmed_schema