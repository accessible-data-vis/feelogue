"""
Data query tool using pandas DataFrame agent.
"""
import pandas as _pd
import numpy as _np
from typing import Annotated

from langchain_openai import ChatOpenAI
from langchain_experimental.agents import create_pandas_dataframe_agent
from langchain.tools import tool
from langgraph.prebuilt import InjectedState

from .utils import format_messages_to_str
from .config import OPENAI_MODEL_ANALYSIS
from .prompts import get_data_query_prefix

# LLM for CSV/data queries - temperature=0 for reliable tool-call compliance
csv_llm = ChatOpenAI(model=OPENAI_MODEL_ANALYSIS, temperature=0, stop=None)

# Cache for the pandas agent executor -- rebuilt only when dataset or columns change.
# A fresh pd.DataFrame is constructed on every layer_data_update (context.py),
# so id(df) alone already changes whenever the dataset changes.
_cached_executor = None
_cached_df_id = None
_cached_columns = None


def _get_executor(df, selected_data, columns_to_use: list, state: dict):
    """Return a pandas agent executor, reusing the cached one when nothing changed."""
    global _cached_executor, _cached_df_id, _cached_columns

    df_id = id(df)
    cols = tuple(columns_to_use)

    if (
        _cached_executor is None
        or df_id != _cached_df_id
        or cols != _cached_columns
    ):
        color_field = state.get("color_field")
        df_columns = state.get("df_columns", [])
        print(f"Building pandas agent executor (columns={cols})")
        _cached_executor = create_pandas_dataframe_agent(
            csv_llm,
            selected_data,
            verbose=True,
            allow_dangerous_code=True,
            agent_type="openai-tools",
            prefix=get_data_query_prefix(color_field, df_columns, df),
            max_iterations=3,
            agent_executor_kwargs={"handle_parsing_errors": True},
        )
        for t in _cached_executor.tools:
            if hasattr(t, "locals") and isinstance(t.locals, dict):
                merged = {}
                if hasattr(t, "globals") and isinstance(t.globals, dict):
                    merged.update(t.globals)
                merged.update(t.locals)
                merged.setdefault("pd", _pd)
                merged.setdefault("np", _np)
                t.locals = merged
                if hasattr(t, "globals"):
                    t.globals = merged
                break
        _cached_df_id = df_id
        _cached_columns = cols
    else:
        print("Reusing cached pandas agent executor")

    return _cached_executor


@tool
def csv_query_tool(
    query: str,
    state: Annotated[dict, InjectedState],
) -> str:
    """
    Query the currently loaded chart's underlying data to compute exact values.

    Use this whenever answering requires a real number, aggregate, comparison,
    ranking, extreme, or filtered subset that you cannot read directly from the
    chart context or conversation. This is the ONLY way to get true values from
    the data — never answer a numeric/factual question from memory or the preview.

    Args:
        query: A single, self-contained analytical QUESTION in natural language
            (NOT pandas code). A separate execution agent translates it into
            pandas, runs it against the real DataFrame, and returns the computed
            result. State the operation explicitly: the target column, the
            aggregation (sum/mean/count/max/min/corr...), any filters, grouping,
            and sort order. Reference columns and category/series values by their
            EXACT names as they appear in the schema and grounded value lists —
            do not paraphrase, pluralize, or invent names. Ask for one complete
            thing per call.

    Returns:
        On success: the computed value(s) as a short string (e.g. "Mean: 840.71").
        When a filter matches no rows (e.g. a series/category that isn't in the
        data): a line beginning "NOT_FOUND:" naming what was missing — treat this
        as "no such data", NOT as zero, and do not report it to the user verbatim.
        If no data is loaded or an error occurs: a plain-language message saying so.
    """
    print("CSV Tool Query: \n", query)
    try:
        from .context import get_df
        df = get_df()
        if df is None or df.empty:
            return (
                "I don't have any chart data loaded right now. "
                "Please load a chart first, and then ask your question again."
            )

        x_field = state.get("x_field") or (df.columns[0] if len(df.columns) > 0 else None)
        y_field = state.get("y_field") or (df.columns[-1] if len(df.columns) > 1 else None)
        chart_type = (state.get("chart_type") or "").lower()
        color_field = state.get("color_field")
        second_column = state.get("second_column")

        if chart_type in {"bar", "line"} and x_field and y_field and {x_field, y_field}.issubset(df.columns):
            columns_to_use = [x_field, y_field]
            if color_field and color_field in df.columns:
                columns_to_use.append(color_field)
        elif chart_type == "scatter" and second_column and second_column in df.columns:
            columns_to_use = [x_field, y_field, second_column]
        else:
            columns_to_use = list(df.columns)

        if "visible" in df.columns and not df["visible"].all() and "visible" not in columns_to_use:
            columns_to_use.append("visible")

        selected_data = df[columns_to_use]
        executor = _get_executor(df, selected_data, columns_to_use, state)

        chat_history = format_messages_to_str(state.get("messages"))
        result = executor.invoke({"input": query, "chat_history": chat_history})
        if isinstance(result, str):
            return result
        output = result.get("output")
        if output:
            return output
        return "I wasn't able to get a result for that query."

    except Exception as e:
        print(f"csv_query_tool error: {type(e).__name__}: {e}")
        return f"I encountered an error while processing your query: {str(e)}"
