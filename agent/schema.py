
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
        },
        "requested": {
            "type": "string",
            "description": "The chart the user asked for, as heard, without the loading verb (e.g. 'interest rats' from 'load the interest rats chart'). Spoken back when nothing matches, so the user can catch a mishearing."
        }
    },
    "required": ["matches", "requested"],
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
              "user_help",
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
                "The `_id` of every row the answer refers to (e.g. the row behind a "
                "max/min/specific-date answer, a peak and a low, the rows being compared), "
                "copied verbatim from a csv_query_tool result - never invented - plus the id "
                "of any axis tick the answer names (`x-axis-tick-<i>` / `y-axis-tick-<i>`, "
                "from the Axis ticks block). A whole series goes in `highlighted_series`, "
                "never here. Empty when the answer refers to no specific row or tick."
            ),
        },
        "highlighted_series": {
            "type": "array",
            "items": {"type": "string"},
            "description": (
                "The exact name of every WHOLE series the answer refers to (its trend, its "
                "overall shape, an aggregate over all of it). The system marks every point of "
                "each series itself. A series named with a feature or period ('Brisbane's "
                "peak') is not a whole series: highlight those rows by id. Empty when the "
                "answer refers to no whole series."
            ),
        },
    },
    "required": ["message", "highlighted_ids", "highlighted_series"],
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
