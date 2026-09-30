"""
Intent classification for user queries.
Merges intent classification and deictic detection into a single LLM call.
"""
from .client import client
from .config import OPENAI_MODEL_CLASSIFIER
from .prompts import get_intent_classification_prompt, INTENT_CLASSIFIER_SYSTEM_PROMPT
from .utils import parse_llm_json
from .schema import INTENT_SCHEMA




def classify_query(user_query: str, messages: list = []) -> dict:
    """
    Classify user intent(s) and detect deictic references in a single call.
    Passes the whole conversation history so the classifier can resolve
    follow-up references like "what about Q3?" correctly.

    Returns:
        dict with keys:
            - intents: list of {type, query} dicts
            - has_deictic: boolean
    """

    resp = client.chat.completions.create(
        model=OPENAI_MODEL_CLASSIFIER,
        messages=[
            {"role": "system", "content": INTENT_CLASSIFIER_SYSTEM_PROMPT},
            {"role": "user", "content": get_intent_classification_prompt(user_query, messages=messages)},
        ],
        temperature=0,
        response_format={
        "type": "json_schema",
        "json_schema": {
            "name": "intent_classification",
            "schema": INTENT_SCHEMA
        }
    })

    raw = (resp.choices[0].message.content or "").strip()
    result = parse_llm_json(raw, fallback={"intents": [{"type":"general_question", "query":user_query}], "has_deictic": False})
    # Validate intents
    valid_intents = {
        "load_chart", "chart_overview",
        "touch_interaction", "trend", "filter",
        "data_analysis", "general_question",
    }
    raw_intents = result.get("intents", result.get("type", [{"type":"general_question", "query":user_query}]))

    validated_intents = []
    for intent in raw_intents:
        intent_type = intent.get("type")
        query = intent.get("query")
        if intent_type in valid_intents:
            validated_intents.append({"type":intent_type, "query":query})
        else:
            # Try to match partial
            for v in valid_intents:
                if v in intent_type.lower():
                    validated_intents.append({"type": v, "query": query})
                    break

    if not validated_intents:
        validated_intents = [{"type":"general_question", "query":user_query}]

    # These act on the whole chart once per utterance. The model sometimes splits
    # "put GPU back on" in two, and a second copy would overwrite the first's command.
    single_shot = {"filter", "load_chart", "chart_overview"}
    seen = set()
    deduped = []
    for intent in validated_intents:
        if intent["type"] in single_shot:
            if intent["type"] in seen:
                continue
            seen.add(intent["type"])
        deduped.append(intent)
    validated_intents = deduped

    has_deictic = result.get("has_deictic") is True
    return {
        "intents": validated_intents,
        "has_deictic": has_deictic,
    }


def classify_intent(user_query: str) -> dict:
    """The first intent ({"type", "query"}) for a query; used by agent.ipynb."""
    return classify_query(user_query)["intents"][0]
