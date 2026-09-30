"""Intent classification. Consumed by agent/intent.py."""
from ..utils import format_messages_to_str


# =============================================================================
# Intent Classification
# =============================================================================

INTENT_CLASSIFIER_SYSTEM_PROMPT = (
    "You are a query classifier for a chart visualization system. "
    "Return only valid JSON in lower_case. "
    "The conversation history above (if any) shows what the user and assistant discussed previously. "
    "Use that context to resolve vague or follow-up queries -- for example, "
    "'what about Q3?' after a Q1 discussion should be classified as data_analysis about Q3."
)


def get_intent_classification_prompt(user_query: str, messages: list[dict] | None = None) -> str:
    """Prompt for classifying user intent and detecting deictic references."""
    history_block = format_messages_to_str(messages=messages or [])
    if history_block:
        history_block = ("\nHere is the message history block, use this to resolve any referents "
                         "from the current query (after history):" + history_block)

    return f"""
You are a query classifier for a chart visualization system, the query that you will get is from an audio transcription,
Pay attention to what the user actually wants.

Return JSON with two fields:
1. "intent" - a dictionary of one or more of:
   - load_chart: requests or command to load, display, plot, or switch to a different dataset or chart (e.g., "show the sales chart", "load GPU prices", "display the bar chart"). Not for showing or hiding a series of the chart already loaded: that is filter.

   - chart_overview: asks for an overview of the WHOLE currently loaded chart, which starts the chart's guided overview (e.g., "give me an overview", "walk me through the chart", "describe this chart", "what am I looking at?", "what does this show?"). Only for the chart as a whole. Do NOT use for questions about specific elements (e.g., "first line", "this bar", "highest point") -- those belong to data_analysis -- nor for summaries of one series, period, or part of the data (e.g., "summarize sales in 2021", "how did Melbourne change?") -- those are data_analysis or trend.

   - touch_interaction: references something touched/highlighted on the chart

   - data_analysis: comparisons, calculations, or statistics on the data -- including aggregates (avg/min/max), distributions (t-distribution, histogram), correlations, regressions, or any quantitative analysis ONLY for calculations that can be done using python pandas. Also questions that name a series by its colour ("the blue line"): the data step resolves the colour itself.

   - trend: patterns, trends, or changes over time

   - filter: show or hide whole data series on the current chart, including bringing a hidden series back (e.g. "filter everything apart from Storage", "hide GPU", "only show Memory", "show Memory again", "put GPU back", "show all the series again")

   - general_question: anything else, including general questions about how chart types work (e.g., "how do I read a bar chart?", "what is a scatterplot?"), and requests to move, enlarge, or otherwise change the chart's view (the view is fixed)
    Choose the MOST specific intent(s). If multiple apply, include all relevant intents, but avoid over-classifying.    
    Separate out the query for each intent into "intent":"query". 
    For each intent, return "spans": a list of EXACT substrings copied verbatim from the
    CURRENT user utterance only. Do not paraphrase, complete, correct, translate, or add
    any word that is not present in the current utterance.

    To carry a shared subject across intents (e.g. "the blue line"), repeat that subject as
    its own span in each intent. The subject MUST appear in the current utterance — never
    invent one or pull it from earlier turns.

    Conversation history is provided ONLY to help you choose the intent TYPE. You must NOT
    copy any entity, phrasing, or fact from history into spans.
    Example: "What is the blue color line average"
    Output:
    "intents": [
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
{history_block}
Current utterance. This is the current user's actual query:
<utterance>
{user_query}
</utterance>
""".strip()
