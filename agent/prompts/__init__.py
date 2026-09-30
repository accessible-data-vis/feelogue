"""Prompt library: one module per intent, plus common.py for what they share.

Every symbol the rest of the agent uses is re-exported here, so
`from .prompts import X` works; import from the submodule directly when you want
the dependency to be visible.

  common.py                 voicing rules, date granularity, the derived date column
  intent_classification.py  agent/intent.py
  chart_overview.py         agent/layer_overview.py
  data_query.py             agent/data_query.py, agent/graph.py
  filtering.py              agent/filtering.py
  postprocessing.py         agent/postprocessing.py
  chunk_assignment.py       agent/postprocessing.py
  image_tool.py             agent/image_tool.py
  chart_loader.py           agent/chart_loader.py

The prompt bodies are prompt-cache prefixes. Edit the text in its module, and
don't reflow, re-indent or reorder it for tidiness.
"""

from .common import (
    get_date_granularity_rule,
    get_derived_date_column_rule,
    get_spoken_format_whitelist,
)
from .intent_classification import (
    INTENT_CLASSIFIER_SYSTEM_PROMPT,
    get_intent_classification_prompt,
)
from .chart_overview import (
    LAYER_OVERVIEW_SYSTEM_PROMPT,
)
from .data_query import (
    get_data_query_prefix,
    get_data_query_scope,
    get_data_query_system_prompt,
)
from .filtering import (
    FILTER_EXTRACTION_SYSTEM_PROMPT,
    get_filter_extraction_prompt,
)
from .postprocessing import (
    get_combine_multi_intent_responses_prompt,
    get_rewrite_list_prompt,
)
from .chunk_assignment import get_chunk_assignment_prompt
from .image_tool import IMAGE_TOOL_SYSTEM_PROMPT
from .chart_loader import (
    get_load_chart_prompt,
    get_load_chart_system_prompt,
)

__all__ = [
    # Shared
    "get_date_granularity_rule",
    "get_derived_date_column_rule",
    "get_spoken_format_whitelist",
    # Intent classification
    "INTENT_CLASSIFIER_SYSTEM_PROMPT",
    "get_intent_classification_prompt",
    # Layered presentation
    "LAYER_OVERVIEW_SYSTEM_PROMPT",
    # Data query
    "get_data_query_prefix",
    "get_data_query_scope",
    "get_data_query_system_prompt",
    # Filtering
    "FILTER_EXTRACTION_SYSTEM_PROMPT",
    "get_filter_extraction_prompt",
    # Post-processing
    "get_combine_multi_intent_responses_prompt",
    "get_rewrite_list_prompt",
    # Chunk assignment
    "get_chunk_assignment_prompt",
    # Image tool
    "IMAGE_TOOL_SYSTEM_PROMPT",
    # Chart loading
    "get_load_chart_prompt",
    "get_load_chart_system_prompt",
]
