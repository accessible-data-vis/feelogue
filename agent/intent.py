"""
Intent classification for user queries.
Merges intent classification and deictic detection into a single LLM call.
"""
from .client import client
from .config import OPENAI_MODEL_CLASSIFIER
from .prompts import get_intent_classification_prompt, INTENT_CLASSIFIER_SYSTEM_PROMPT
from .utils import parse_llm_json
from .schema import INTENT_SCHEMA




def classify_query(user_query: str, has_image: bool = False) -> dict:
    """
    Classify user intent(s) AND detect deictic references in a single call.

    Returns:
        dict with keys:
            - intents: Dictionary of {intent:query} pairs with query seperated independently based on intent
            - has_deictic: boolean
    """
    resp = client.chat.completions.create(
        model=OPENAI_MODEL_CLASSIFIER,
        messages=[
            {"role": "system", "content": INTENT_CLASSIFIER_SYSTEM_PROMPT},
            {"role": "user", "content": get_intent_classification_prompt(user_query)},
        ],
        temperature=0,
        response_format={
        "type": "json_schema",
        "json_schema": {
            "name": "intent_classification",
            "schema": INTENT_SCHEMA
        }
    }

    )

    raw = (resp.choices[0].message.content or "").strip()
    result = parse_llm_json(raw, fallback={"intents": [{"type":"general_question", "query":user_query}], "has_deictic": False})
    # Validate intents
    valid_intents = {
        "load_chart", "chart_overview", "image_analysis",
        "touch_interaction", "trend", "operations",
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

    has_deictic = result.get("has_deictic") is True
    return {
        "intents": validated_intents,
        "has_deictic": has_deictic,
    }


# Keep old functions for backwards compatibility (they now use the merged call)
def classify_intent(user_query: str, has_image: bool = False) -> str:
    """Legacy function - returns just the first intent."""
    return classify_query(user_query, has_image)["intents"][0]

def detect_deictic_reference(user_query: str) -> bool:
    """Legacy function - returns just the deictic flag."""
    return classify_query(user_query)["has_deictic"]


def normalize_intent(intent: str, valid_intents: list[str]) -> str:
    if intent in valid_intents:
        return intent
    for v in valid_intents:
        if v in intent.lower():
            return v
    return "general_question"