"""
Data query tool using pandas DataFrame agent.
"""
from langchain_openai import ChatOpenAI
from langchain_experimental.agents import create_pandas_dataframe_agent
from langchain.tools import tool

from .context import agent_context
from .config import OPENAI_MODEL_ANALYSIS
from .prompts import get_data_query_prefix

# LLM for CSV/data queries - temperature=0 for reliable tool-call compliance
csv_llm = ChatOpenAI(model=OPENAI_MODEL_ANALYSIS, temperature=0, stop=None)

# Cache for the pandas agent executor — rebuilt only when dataset or columns change
_cached_executor = None
_cached_version = None
_cached_df_id = None
_cached_columns = None


def _get_executor(df, selected_data, columns_to_use: list):
    """
    Return a pandas agent executor, reusing the cached one when the dataset
    and column selection haven't changed.

    Uses id(df) as a fingerprint — since update_dataframe_from_layer always
    creates a new DataFrame object, the id changes on every data update,
    making cache invalidation reliable regardless of dataset_version.
    """
    global _cached_executor, _cached_version, _cached_df_id, _cached_columns

    version = agent_context.get("dataset_version")
    df_id = id(df)
    cols = tuple(columns_to_use)

    if (
        _cached_executor is None
        or version != _cached_version
        or df_id != _cached_df_id
        or cols != _cached_columns
    ):
        print(f"Building pandas agent executor (version={version}, columns={cols})")
        _cached_executor = create_pandas_dataframe_agent(
            csv_llm,
            selected_data,
            verbose=True,
            allow_dangerous_code=True,
            agent_type="openai-tools",
            prefix=get_data_query_prefix(),
            max_iterations=6,
            agent_executor_kwargs={"handle_parsing_errors": True},
        )
        _cached_version = version
        _cached_df_id = df_id
        _cached_columns = cols
    else:
        print("Reusing cached pandas agent executor")

    return _cached_executor


@tool
def csv_query_tool(query: str) -> str:
    """
    Handles general queries on the currently loaded chart data.
    Uses the DataFrame stored in agent_context["df"], filters to relevant columns,
    and delegates to a pandas DataFrame agent with python_repl_ast.
    """
    try:
        df = agent_context.get("df")
        if df is None or df.empty:
            return (
                "I don't have any chart data loaded right now. "
                "Please load a chart first, and then ask your question again."
            )

        x_field = agent_context.get("x_field", df.columns[0])
        y_field = agent_context.get("y_field", df.columns[-1])
        chart_type = (agent_context.get("chart_type") or "").lower()
        second_column = agent_context.get("second_column")

        # Decide which columns are relevant for this chart type
        color_field = agent_context.get("color_field")
        if chart_type in {"bar", "line"} and {x_field, y_field}.issubset(df.columns):
            columns_to_use = [x_field, y_field]
            if color_field and color_field in df.columns:
                columns_to_use.append(color_field)
        elif chart_type == "scatter" and second_column and second_column in df.columns:
            columns_to_use = [x_field, y_field, second_column]
        else:
            columns_to_use = list(df.columns)

        # Include 'visible' only when some rows are actually hidden — if all data is
        # visible, omitting the column prevents the agent from mentioning it unnecessarily
        if "visible" in df.columns and not df["visible"].all() and "visible" not in columns_to_use:
            columns_to_use.append("visible")

        selected_data = df[columns_to_use]
        executor = _get_executor(df, selected_data, columns_to_use)

        result = executor.invoke({"input": query})
        if isinstance(result, str):
            return result
        output = result.get("output")
        if output:
            return output
        return "I wasn't able to get a result for that query."

    except Exception as e:
        print(f"csv_query_tool error: {type(e).__name__}: {e}")
        return f"I encountered an error while processing your query: {str(e)}"
