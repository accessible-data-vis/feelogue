
# Formatted output for ChatGPT response
INTENT_SCHEMA={
  "type": "object",
  "properties": {
    "intents": {
      "type": "array",
      "description": "List of detected user intents, ordered by priority (most important first)",
      "items": {
        "type": "object",
        "properties": {
          "type": {
            "type": "string",
            "enum": [
              "load_chart",
              "image_analysis",
              "chart_overview",
              "touch_interaction",
              "data_analysis",
              "trend",
              "operations",
              "general_question"
            ],
            "description": "The classified intent type"
          },
          "query": {
            "type": "string",
            "description": "A fully self-contained query specific to this intent"
          }
        },
        "required": ["type", "query"],
        "additionalProperties": False
      },
      "minItems": 1
    },
    "has_deictic": {
      "type": "boolean",
      "description": "True if the query includes explicit deictic references like 'this', 'that', or touched elements"
    }
  },
  "required": ["intents", "has_deictic"],
  "additionalProperties": False
}