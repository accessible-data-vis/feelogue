
LOAD_CHART_SCHEMA = {
    "type": "object",
    "properties": {
        "matches": {
            "type": "array",
            "description": "Charts that match the user's request, ordered by relevance (best match first). Empty if nothing matches.",
            "items": {
                "type": "object",
                "properties": {
                    "chart_id": {
                        "type": "integer",
                        "description": "The unique ID of the chart"
                    },
                    "chart_name": {
                        "type": "string",
                        "description": "The display name of the chart"
                    }
                },
                "required": ["chart_id", "chart_name"],
                "additionalProperties": False
            }
        }
    },
    "required": ["matches"],
    "additionalProperties": False
}


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
              "chart_overview",
              "touch_interaction",
              "data_analysis",
              "trend",
              "filter",
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


# Data-query answers carry the `_id`s of the rows they're anchored to, chosen in
# the same call that writes the spoken answer. Unity stamps an `_id` on every row.
DATA_QUERY_SCHEMA = {
    "type": "object",
    "properties": {
        "message": {
            "type": "string",
            "description": "The spoken answer to the user's question (concise, TTS-friendly, no markdown).",
        },
        "highlighted_ids": {
            "type": "array",
            "items": {"type": "string"},
            "description": (
                "The `_id` of each row this answer is anchored to (e.g. the row behind a "
                "max/min/specific-date answer), copied verbatim from a csv_query_tool result "
                "- never invented. Empty when the answer is not tied to specific rows "
                "(e.g. an aggregate or general statement)."
            ),
        },
    },
    "required": ["message", "highlighted_ids"],
    "additionalProperties": False,
}


# Maps already-resolved highlight nodes (by id) to the spoken chunk that
# references each one; drives when each point blinks.
CHUNK_ASSIGNMENT_SCHEMA = {
    "type": "object",
    "properties": {
        "assignments": {
            "type": "array",
            "description": "One entry per data point id; several ids may share a chunk.",
            "items": {
                "type": "object",
                "properties": {
                    "id": {"type": "string", "description": "The data point's id, copied exactly."},
                    "chunk": {
                        "type": ["integer", "null"],
                        "description": "Index of the sentence that references this point, or null.",
                    },
                },
                "required": ["id", "chunk"],
                "additionalProperties": False,
            },
        }
    },
    "required": ["assignments"],
    "additionalProperties": False,
}
