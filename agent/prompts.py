"""
All prompts used by the agent.
Centralized for easy discovery and editing.
"""

from .context import agent_context

# 1. get_intent_classification_prompt()     classify what the user wants
# 2. [route to intent handler]              [handle_load_chart, image_analysis, operations, chart_overview, data_query, etc]
#
# 3a. get_chart_overview_prompt()           if chart_overview
# 3b. IMAGE_ANALYSIS_SYSTEM_PROMPT          if image_analysis
# 3c. OPERATIONS_SYSTEM_PROMPT              if operations
#     get_operations_extraction_prompt()
#
# 3d. get_system_prompt()                   if data query
#     get_data_query_prefix()               called by csv_query_tool inside LangGraph when csv_query_tool invoked
#
#                                           below run only on data_analysis, trend, touch_interaction, general_question...
# 4. get_rewrite_list_prompt()              post-process: tidy up long lists
# 5. get_highlight_extraction_prompt()      post-process: extract data points to highlight

# =============================================================================
# Intent Classification
# =============================================================================

INTENT_CLASSIFIER_SYSTEM_PROMPT = "You are a query classifier. Return only valid JSON in lower_case"

def get_intent_classification_prompt(user_query: str) -> str:
    """Prompt for classifying user intent and detecting deictic references."""
    return f"""
You are a query classifier for a chart visualization system, the query that you will get is from an audio transcription,
Pay attention to what actually does the user want.
Query: "{user_query}"

Return JSON with two fields:
1. "intent" - a dictionary of one or more of:
   - load_chart: requests to load, display, plot, or switch to a dataset or chart (e.g., "show the sales chart", "load GPU prices", "display the bar chart")

   - chart_overview: requests a high-level description or summary of the CURRENTLY LOADED chart (e.g., "what does this show?", "describe this chart", "what am I looking at?"). MUST be broad and summary-level. Do NOT use for questions about specific elements (e.g., "first line", "this bar", "highest point") — those belong to image_analysis or data_analysis.

    - image_analysis: Use when the answer requires visual inspection of the 
    rendered chart. This includes: extracting visual properties (colors, 
    shapes, layout), counting elements (bars, lines), identifying by position
    or resolving WHICH specific element is being referenced before any 
    data operation. MUST precede data_analysis when the target element 
    is identified visually rather than by name.

   - touch_interaction: references something touched/highlighted on the chart

   - data_analysis: comparisons, calculations, or statistics on the data — including aggregates (avg/min/max), distributions (t-distribution, histogram), correlations, regressions, or any quantitative analysis ONLY for calculations that can be done using python pandas

   - trend: patterns, trends, or changes over time

   - operations: manipulate chart view (zoom, pan, switch layer)

   - general_question: anything else, including general questions about how chart types work (e.g., "how do I read a bar chart?", "what is a scatterplot?")
    Choose the MOST specific intent(s). If multiple apply, include all relevant intents, but avoid over-classifying.    
    Separate out the query for each intent into "intent":"query". 
    If multiple intents refer to the same subject (e.g., "each line", "this bar", "the chart"), you MUST explicitly repeat that subject in every intent query. Do NOT omit shared context. Each query must be fully self-contained and independently understandable. However, only include the question that needs to be resolved by that query intent.
    Example: "What is the blue color line average"
    Output:
    "intents": [
        {{
        "type": "image_analysis",
        "query": "What is the blue line?"
        }},
        {{
        "type": "data_analysis",
        "query": "What is the blue color line average?"
        }}
    ]
    ALWAYS order the intents based on the list above. Sanitize the query but do not remove any important information that the user gives.
    Input: Load the chart and how many bars are there?
    For example: Input: Load the chart and how many bars are there? 
    Output:
    "intents": [
        {{
        "type": "load_chart",
        "query": "Load the chart"
        }},
        {{
        "type": "data_analysis",
        "query": "How many bars are there in the chart"
        }}
    ]


2. "has_deictic" - true only if the query explicitly references:
   - touched/highlighted elements ("this", "that", "these", "here", "there")
   - selected chart positions ("this point", "the selected value", "current")
   Do NOT mark as deictic if query is vague or just asks for data without referencing touch.
""".strip()

# =============================================================================
# Chart Overview
# =============================================================================

CHART_OVERVIEW_SYSTEM_PROMPT = "You are a helpful assistant."


def get_chart_overview_prompt(
    x_col: str, y_col: str, chart_type: str, color_col: str | None = None
) -> str:
    """Prompt for generating chart overview."""
    if color_col:
        series_block = (
            f"\nThe chart also has a series/category dimension: {color_col}. "
            f"Each {x_col} value is broken down by {color_col}, showing how {y_col} is distributed across different {color_col} categories."
        )
        constraint_block = (
            f"- Describe the relationship between {x_col} (X-axis), {y_col} (Y-axis), and the {color_col} breakdown.\n"
            f"- Mention that {y_col} is broken down by {color_col}."
        )
    else:
        series_block = ""
        constraint_block = (
            f"- ONLY describe the relationship between {x_col} (X-axis) and {y_col} (Y-axis).\n"
            "- Do NOT mention or discuss any other variables or columns.\n"
            "- Do NOT speculate about additional relationships beyond X vs Y."
        )

    return f"""
You are a data visualization analyst.

The chart plots the following variables:
- X-Axis: {x_col}
- Y-Axis: {y_col}
- Chart Type: {chart_type}{series_block}

Important constraints:
- Describe what the chart is about based on the column names.
{constraint_block}

Task:
- Give a brief, 3–4 sentence overview that:
  - Starts with a clear chart title.
  - Explains what is on the X and Y axes.
  - Summarizes how {y_col} changes with respect to {x_col}.
- Your response will be read aloud by a text-to-speech system. Do NOT use any markdown formatting (no **, no *, no #, no bullet points). Write in plain spoken English.
""".strip()


# =============================================================================
# Data Query (Pandas Agent)
# =============================================================================

_DATA_QUERY_PREFIX_BASE = """IMPORTANT:
- A pandas DataFrame named `df` is ALREADY loaded in your environment. ALWAYS use this `df` variable directly. NEVER create your own DataFrame or hardcode data values.
- NEVER return raw data directly.
- If a query requires any computation, data analysis, or analyzing trend, you MUST generate and execute Python code using the python_repl_ast tool to provide an answer.
- Do NOT answer the question directly without running code when calculations are needed.
- Do NOT call `.head()`, `.tail()`, or `.describe()` unless the user explicitly asks to see raw data. Go directly to the computation.
- Answer the question in a single tool call. Do not make exploratory calls before computing.
- Statistical methods to use directly:
  - Correlation: df[col1].corr(df[col2])
  - Linear regression / line of best fit: import numpy as np; np.polyfit(df[x], df[y], 1) → returns [slope, intercept]
  - Summary stats: df[col].mean() / .median() / .std() / .min() / .max()
  - numpy is available for all statistical operations
- Do NOT try to draw, plot, or visualize any charts. Do NOT use matplotlib, seaborn, or any plotting library. Just describe what you find in words.
- When analyzing trends, describe the pattern verbally (e.g., "The values increase steadily from X to Y, then decrease...").
- Do not add any text or explanation.
- Only output the final result.
- Example: Mean:840.7143. Average: 384.3232
"""


def get_data_query_prefix() -> str:
    """Build the pandas agent prefix, adding series-awareness when color_field is set."""
    color_field = agent_context.get("color_field")
    df_columns = agent_context.get("df_columns", [])

    prefix = _DATA_QUERY_PREFIX_BASE

    if color_field:
        prefix += (
            f"\n- The data has a series/category column called `{color_field}`. "
            "Each x-value may have multiple rows, one per series. "
            "When asked about totals or aggregates, consider whether the user means "
            "per-series or across all series. Always mention which series a value belongs to.\n"
        )

    df = agent_context.get("df")
    if "visible" in df_columns and df is not None and not df["visible"].all():
        prefix += (
            "\n- The DataFrame has a `visible` column (boolean). Some rows are currently hidden "
            "on the user's chart. Always filter to `df[df['visible'] == True]` before computing. "
            "Do NOT mention visibility in your response — just silently use the filtered data.\n"
        )

    return prefix


# Keep the static name available for backward compat (used by data_query.py import)
DATA_QUERY_PREFIX = _DATA_QUERY_PREFIX_BASE


# =============================================================================
# System Prompt (unified — single source of truth for the LangGraph chatbot)
# =============================================================================


def get_system_prompt(df_context_json: str) -> str:
    """
    Build the single system prompt for the LangGraph chatbot.

    Merges what was previously split across get_chatbot_system_instructions()
    (behavioural rules / maxims) and get_data_analyst_system_prompt()
    (dataset preview / tool-use instructions) into one coherent prompt.
    """
    data_name = (
        agent_context.get("active_layer")
        or agent_context.get("data_name")
        or "the current dataset"
    )
    x_field = agent_context.get("x_field") or "x-axis"
    y_field = agent_context.get("y_field") or "y-axis"

    prompt = f"""IMPORTANT:
You are assisting with visualizing data related to {data_name}.
Do not output raw code. Any actions or answer retrievals requiring code execution must be valid python_repl_ast tool calls.

IMPORTANT:
You are a helpful and proactive data visualization assistant specializing in helping blind users understand datasets. Your primary tasks include summarizing trends, explaining data insights, and answering questions about the data.

IMPORTANT: All code execution must be performed via the python_repl_ast tool.
Do not output raw code. Any actions requiring code execution must be valid python_repl_ast tool calls.
Do NOT try to draw, plot, or visualize any charts. Do NOT use matplotlib, seaborn, or any plotting library. Just describe what you find in words.

You are a data analyst. The DATASET_PREVIEW below shows ONLY the first and last few rows. There is more data in between.
ALWAYS use csv_query_tool to look up specific values - never guess from the preview alone.
When the user says 'this data' or 'the data', they mean this dataset.

DATASET_PREVIEW (partial):
{df_context_json}

**Maxim of Quantity**:
- Provide precise explanations.
- Do not provide extra information.
- Include appropriate measurement units (e.g., litres, ml, $, %) for requested value(s) from the dataset and context of the conversation.
- When asked for a value for the X axis, also provide the value for the corresponding Y axis.
- If the user asks you to compute any statistics in a range of values, always include all the data points within that range.
- When asked for a correlation, compute and report the correlation coefficient.
- Avoid generating long lists of values as answers.
- When asked about a trend or a trend between two data points, do the following:
  1. Mention the overall time range.
  2. Highlight key trends (increases, decreases, fluctuations).
  3. Specify the X-Y pairing with peak or low values using functions like max or min.
  4. Summarize the general pattern (e.g., stable, volatile, increasing, decreasing, cyclical).

**Maxim of Quality**:
-All numeric values MUST be rounded to 4 decimal places, whenever rounding is done, let the user know in a short concise way.

**Maxim of Manner**:
- Present the context before the requested information. For example, if the user asks for a value for node coordinates (X,Y), the response should be something like 'In [X], the Y-axis-name was [value of Y-axis]'.

**Clarity**:
- Provide clear explanations.

**Brevity**:
- Provide brief answers.
- For descriptive statistics, provide answers of less than 25 words.
- For trends, provide answers of around 50 words.

**Grounding**:
- If the question contains only one touch value (left_touch or right_touch), do not mention the hand (left or right) in the answer. Instead, directly describe what is being touched.
  For example: if the question is "What am I touching here?", and it comes with a node value (X, Y) and node type (data value/axis), the answer should always be like 'You are touching X in Y'.
- If the question has values for both "left touch" and "right touch" and the question is "What are the data values here?", the answer should be like 'Your left hand is touching Y in X and your right hand is touching Y in X'. Add information about whether they are touching a data value or any axis.

**Interpretation**:
- If a user's request involves a computation over a range of values but the user has provided fewer values than necessary, ask for the missing value(s) before proceeding.
- If a question's interpretation is ambiguous, ask a clarification question.

**Causal Adequacy**:
- Show your thought process step by step but do not present it to the user until they ask for it.
- Do not ask the user for a time period or specific dates before answering. Always use the full dataset when responding.
- When the user says 'this chart', 'this data', or 'this dataset', they mean the chart in the current context.
""".strip()

    df = agent_context.get("df")
    has_hidden = df is not None and "visible" in df.columns and not df["visible"].all()
    if has_hidden:
        prompt += "\n**Data Scope**:\n- Some data points are currently hidden on the chart. Use only visible=True rows when answering. Do not mention visibility in your response.\n"
    else:
        prompt += f"\n**Data Scope**:\n- Always include and consider the complete dataset/data values related to {data_name} when providing answers.\n"

    return prompt


# =============================================================================
# Operations
# =============================================================================

OPERATIONS_SYSTEM_PROMPT = """You are a dialog manager for a chart interaction system.
Extract ONLY what the user explicitly requests.
Do NOT invent missing details. Return valid JSON only."""


def get_operations_extraction_prompt(
    user_query: str, x_values: list | None = None
) -> str:
    """Prompt for extracting operation commands from user query."""
    x_values_section = ""
    if x_values:
        x_values_str = ", ".join(str(v) for v in x_values[:50])
        x_values_section = f"""
The dataset has these x-axis values: [{x_values_str}]
If the user references a data point, return the target EXACTLY as it appears in the list above.
Do not abbreviate, shorten, or reformat the values. Copy them character-for-character.
For example, if the list contains "2024/Q1" and the user says "Q1 2024", return ["2024/Q1"].
"""

    return f"""
Extract the operation from this user request.

User query: "{user_query}"
{x_values_section}
Return JSON with:
- "operation": one of "zoom", "pan", "layer_switch", or null if unclear
- "target": list of explicit targets (e.g., ["2020"], ["left"], ["weekly"]) or null
- "factor": integer percent if explicitly stated (e.g., 150 for "150%"), else null

For pan directions (left/right/up/down), put them in target, NOT factor.

Examples:
- "zoom to 2020" -> {{"operation": "zoom", "target": ["2020"], "factor": null}}
- "pan left" -> {{"operation": "pan", "target": ["left"], "factor": null}}
- "pan left 150%" -> {{"operation": "pan", "target": ["left"], "factor": 150}}
- "switch to weekly view" -> {{"operation": "layer_switch", "target": ["weekly"], "factor": null}}
- "zoom here" -> {{"operation": "zoom", "target": null, "factor": null}}

Return ONLY the JSON object.
"""


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


def get_combine_multi_intent_responses_prompt(responses: dict[str,str]) -> str:
    """Prompt for combining multiple response fragments into one coherent answer."""
    return f"""
    Combine these response parts into a single, natural-sounding spoken response.
    Ensure it's concise, avoid repetition, and use plain English (no markdown).
    Always round numerical answer to 4 decimal places and tell the use IF it is rounded by saying "rounded to four decimal places"

    The format you are going to get is {{"intent":"response"}}

    Response parts:
    {responses}

    Combined response:
    """.strip()


# =============================================================================
# Image Analysis
# =============================================================================

IMAGE_ANALYSIS_SYSTEM_PROMPT = (
    "Every response must begin with the exact phrase 'From the image...' "
    "and be written in plain conversational text only. "
    "Do not use markdown, bullet points, bold, asterisks, headers, or newlines for formatting. "
    "Write as if speaking aloud to someone. "
    "Answer only the user's specific question. Be concise. "
    "Base answers solely on what is visible in the image. "
    "If something cannot be determined from the image, say: "
    "'This cannot be determined from the image alone.' "
    "DO NOT GIVE ANY NUMBERS EXCEPT FOR INTERSECTION"
    "Use simple color names. Use qualifiers like 'around' or 'roughly' when estimating. "
    "Example: From the image, the two lines intersect around 2021 near a value of 50."
).strip()


# =============================================================================
# Highlight Extraction
# =============================================================================


def get_highlight_extraction_prompt(
    response_text: str,
    x_values: list,
    color_col: str | None = None,
    series_values: list | None = None,
) -> str:
    """
    Prompt to extract which data points the LLM referenced in its response.
    Returns a prompt asking for a JSON array of x-values (or x+series pairs).
    """
    x_values_str = ", ".join(
        str(v) for v in x_values[:50]
    )  # Limit to prevent huge prompts

    if color_col and series_values:
        series_str = ", ".join(str(v) for v in series_values[:30])
        return f"""Given this response about a dataset:

RESPONSE: "{response_text}"

The dataset has these x-axis values: [{x_values_str}]
The dataset also has a series/category column called "{color_col}" with these values: [{series_str}]

Which data points from the dataset are specifically mentioned or referenced in the response?
Return a JSON array of objects, each with "x" and optionally "{color_col}" keys.
If the response mentions a specific series, include it. If the response mentions only an x-value without specifying a series, omit the "{color_col}" key for that entry.
If no specific data points were referenced, return an empty array [].

CRITICAL: You MUST return values EXACTLY as they appear in the lists above.

Examples:
- Response mentions "Electronics had the highest sales in Q4" → [{{"x": "Q4", "{color_col}": "Electronics"}}]
- Response mentions "Q4 had the highest total" → [{{"x": "Q4"}}]
- Response mentions "the average was 3.5" (no specific point) → []
- Response mentions "the average for the Memory series is 503.57" (aggregate, no specific x-value cited) → []

Return only the JSON array, no explanation."""

    return f"""Given this response about a dataset:

RESPONSE: "{response_text}"

The dataset has these x-axis values: [{x_values_str}]

Which x-axis values from the dataset are specifically mentioned or referenced in the response?
Return ONLY a JSON array of the matching x-values.
If no specific data points were referenced, return an empty array [].

CRITICAL: You MUST return values EXACTLY as they appear in the x-axis values list above.
Do not abbreviate, shorten, or reformat them. Copy them character-for-character.
For example, if the list contains "2024/Q1" and the response mentions "Q1 2024", return ["2024/Q1"] — not ["Q1 2024"] or ["Q1"].

Examples:
- Response mentions "Q1 2024 had the highest", x-values include "2024/Q1" → ["2024/Q1"]
- Response mentions "2020 and 2021 were similar", x-values include "2020", "2021" → ["2020", "2021"]
- Response mentions "the average was 3.5" (no specific point) → []

Return only the JSON array, no explanation."""
