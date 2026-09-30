"""Post-processing: list rewriting and multi-intent combining. Consumed by
agent/postprocessing.py."""


# =============================================================================
# Post-processing
# =============================================================================


def get_rewrite_list_prompt(text: str) -> str:
    """Prompt for rewriting long bulleted lists into sentences."""
    return (
        "Rewrite the answer by replacing the long bullet list with concise sentences, "
        "grouping items in pairs, preserving meaning and thresholds.\n\n"
        f"ANSWER:\n{text}"
    )


def get_combine_multi_intent_responses_prompt(responses: dict[str, str], query: str) -> str:
    """Prompt for combining multiple response fragments into one coherent answer."""
    return f"""
    Combine these response parts into a single, natural-sounding spoken response for Graphy, an accessible data visualisation system for blind and low-vision users.
    The response will be spoken aloud by a TTS service.

    Rules:
    - Frontload the information that directly answers the query; supporting detail comes after.
    - Be concise and avoid repetition across parts.
    - Plain English only: no markdown, no bullet points, no surrounding quotes.
    - Do not add, remove, or alter any factual information. Restructure and merge only.
    - If only one response part is given, just clean it up; do not pad it.
    - Output the combined response only, with no preamble or labels.

    Example:
    Query: Hide GPU and tell me the average price of Memory
    Responses:
    {{
        "filter": "Showing Memory and Storage, with GPU hidden.",
        "data_analysis": "Memory averages 520 Australian dollars."
    }}
    Output: GPU is hidden, so Memory and Storage are showing. Memory averages 520 Australian dollars.

    The input format is {{"intent": "response"}}.

    Query:
    {query}

    Responses:
    {responses}

    Combined response:
    """.strip()
