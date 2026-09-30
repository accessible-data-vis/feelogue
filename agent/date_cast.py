"""
Whole-column datetime casting for the pandas dataframe sub-agent.

Date-like strings ("December 2024") stay object-dtype, so pandas compares them as
text and min/max/sorting pick the wrong row. The x column is cast into a parallel
"shadow" column used only for computation; the original stays untouched so speech
always uses the value in the source file (a prompt rule in agent/prompts/data_query.py
keeps the sub-agent reporting from the original). The format is re-inferred on every
rebuild, since a later update can carry the same column in a different format.
"""
import re

import pandas as pd

SHADOW_PREFIX = "__dt__"


def shadow_col_name(col: str) -> str:
    return f"{SHADOW_PREFIX}{col}"


def is_shadow_col(col: str) -> bool:
    return col.startswith(SHADOW_PREFIX)


def shadow_columns(df: pd.DataFrame) -> tuple[str | None, str | None]:
    """(original_col, shadow_col) for the x-axis shadow column, if present,
    else (None, None). At most one exists, since only `x_field` is cast."""
    for c in df.columns:
        if is_shadow_col(c):
            return c[len(SHADOW_PREFIX):], c
    return None, None


def _try_parse_column(series: pd.Series):
    """Whole-column-or-nothing parse, trying dayfirst=True then False. A raise means
    some row contradicts that format; success means the entire column agrees."""
    # ISO dates are never read day-first: dayfirst=True turns "2021-03-01" into
    # 3 January, and monthly data on the 1st never has a day above 12 to fail it.
    text = series.dropna().astype(str)
    if text.str.match(r"^\d{4}-\d{2}").all():
        try:
            return pd.to_datetime(series, yearfirst=True, dayfirst=False), False
        except (ValueError, TypeError):
            return None, None
    for dayfirst in (True, False):
        try:
            return pd.to_datetime(series, dayfirst=dayfirst), dayfirst
        except (ValueError, TypeError):
            continue
    return None, None


def sync_datetime_columns(df: pd.DataFrame, x_field: str | None) -> pd.DataFrame:
    """Append a `__dt__<x_field>` shadow column when the x column is date-like.
    Only `x_field` is tried: series labels such as month names would parse as dates.
    Mutates and returns `df`."""
    if not x_field or x_field not in df.columns:
        return df

    series = df[x_field]
    if not pd.api.types.is_object_dtype(series):
        return df
    if series.dropna().empty:
        return df

    parsed, _ = _try_parse_column(series)
    if parsed is not None:
        df[shadow_col_name(x_field)] = parsed
    
    return df


# Vega-Lite timeUnits and d3 time-format directives, each with the finest date
# part it shows.
_TIME_UNIT_GRANULARITY = {
    "year": "year", "yearquarter": "quarter", "yearmonth": "month",
    "yearweek": "week", "yearmonthdate": "day", "yearmonthdatehours": "hour",
    "yearmonthdatehoursminutes": "minute",
    "yearmonthdatehoursminutesseconds": "second",
}
_FORMAT_DIRECTIVES = [   # finest first
    ("second", "SLf"), ("minute", "M"), ("hour", "HI"), ("day", "dejaA"),
    ("week", "UWV"), ("month", "bBm"), ("quarter", "q"), ("year", "Yy"),
]
_BARE_YEAR = re.compile(r"^\d{4}$")
_MONTH_YEAR = re.compile(
    r"^(January|February|March|April|May|June|July|August|September|October|"
    r"November|December) \d{4}$")


def stated_granularity(spec: dict | None, x_field: str | None, values) -> tuple[str, str] | None:
    """(granularity, where it is stated) for the x column, or None.

    Taken only from what states it: the chart spec's timeUnit or axis format, or
    values that name their own period ("2019", "February 2025"). Never read off
    an ISO string's shape: a column of "-01" days may be real days.
    """
    x = ((spec or {}).get("encoding") or {}).get("x") or {}
    if x_field and x.get("field") not in (None, x_field):
        x = {}
    unit = x.get("timeUnit")
    if isinstance(unit, dict):
        unit = unit.get("unit")
    if isinstance(unit, str):
        unit = unit.replace("utc", "")
        if unit in _TIME_UNIT_GRANULARITY:
            return _TIME_UNIT_GRANULARITY[unit], f'the chart\'s timeUnit "{x.get("timeUnit")}"'
    fmt = (x.get("axis") or {}).get("format")
    if x.get("type") == "temporal" and isinstance(fmt, str):
        directives = set(re.findall(r"%[-_0]?([A-Za-z])", fmt))
        for granularity, letters in _FORMAT_DIRECTIVES:
            if directives & set(letters):
                return granularity, f'the chart\'s axis format "{fmt}"'
    texts = [str(v) for v in values if v is not None and str(v) != "nan"]
    if texts and all(_BARE_YEAR.match(t) for t in texts):
        return "year", "the values themselves, which are bare years"
    if texts and all(_MONTH_YEAR.match(t) for t in texts):
        return "month", "the values themselves, which name a month and year"
    return None
