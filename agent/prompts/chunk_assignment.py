"""Placing highlighted points on spoken sentences. Consumed by
agent/postprocessing.py."""


# =============================================================================
# Highlight Extraction
# =============================================================================


def get_chunk_assignment_prompt(chunks: list[str], nodes: dict) -> str:
    """Ask which spoken sentence mentions each resolved point (not which points matter)."""
    numbered_chunks = "\n".join(f"{i}: {c}" for i, c in enumerate(chunks))

    def _describe(node: dict) -> str:
        return ", ".join(f"{k}={v!r}" for k, v in node.items() if k not in ("id", "chunk"))

    point_lines = "\n".join(f"{nid}: {_describe(node)}" for nid, node in nodes.items())

    return f"""Given this spoken response, split into numbered sentences:
{numbered_chunks}

And these data points the response is already known to reference, each identified by id:
{point_lines}

For each id above, which numbered sentence mentions it? Your answer controls exactly when that
point lights up on the display: it is only highlighted while its assigned sentence is being
spoken, not before or after -- so match precisely, don't default to a broad or early guess.
A single sentence may mention more than one data point -- assign every id that fits, even to the
same sentence. Use null only for a point the answer as a whole is about rather than any one
sentence: a null point stays highlighted for the whole answer, so null is never a safe default
for a point you could not place.

Return an "assignments" array with exactly one entry per id listed above, using the ids
EXACTLY as they appear -- copy them character-for-character."""
