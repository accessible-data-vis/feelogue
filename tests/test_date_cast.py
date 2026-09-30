"""date_cast: whole-column datetime casting for the x-axis column."""
import pandas as pd

from date_cast import is_shadow_col, shadow_col_name, sync_datetime_columns


def test_month_strings_get_correct_chronological_min_max():
    df = pd.DataFrame({
        "Month": ["December 2024", "March 2025", "December 2025"],
        "Value": [1, 2, 3],
    })
    df = sync_datetime_columns(df, x_field="Month")

    shadow = shadow_col_name("Month")
    assert shadow in df.columns
    assert df[shadow].min() == pd.Timestamp("2024-12-01")
    assert df[shadow].max() == pd.Timestamp("2025-12-01")
    # original column is untouched
    assert list(df["Month"]) == ["December 2024", "March 2025", "December 2025"]


def test_unambiguous_dayfirst_row_wins_regardless_of_position():
    df = pd.DataFrame({"D": ["01/01/2024", "01/01/2024", "13/02/2024"]})
    df = sync_datetime_columns(df, x_field="D")
    assert df[shadow_col_name("D")].iloc[2] == pd.Timestamp("2024-02-13")


def test_genuinely_mixed_format_column_is_left_uncast():
    df = pd.DataFrame({"D": ["13/02/2024", "02/13/2024"]})
    df = sync_datetime_columns(df, x_field="D")
    assert shadow_col_name("D") not in df.columns


def test_non_date_string_column_is_left_uncast():
    df = pd.DataFrame({"Region": ["APAC", "EMEA", "AMER"]})
    df = sync_datetime_columns(df, x_field="Region")
    assert shadow_col_name("Region") not in df.columns


def test_numeric_column_is_a_no_op():
    df = pd.DataFrame({"Value": [1, 2, 3]})
    df = sync_datetime_columns(df, x_field="Value")
    assert shadow_col_name("Value") not in df.columns


def test_all_null_column_is_left_uncast():
    df = pd.DataFrame({"D": [None, None, None]})
    df = sync_datetime_columns(df, x_field="D")
    assert shadow_col_name("D") not in df.columns


def test_missing_values_become_nat_without_raising():
    df = pd.DataFrame({"D": ["2024-01-01", None, "2024-03-01"]})
    df = sync_datetime_columns(df, x_field="D")
    shadow = shadow_col_name("D")
    assert shadow in df.columns
    assert pd.isna(df[shadow].iloc[1])


def test_format_reinferred_fresh_each_call_no_stale_cache():
    """A later call with the same column name but a different format re-infers it."""
    df1 = pd.DataFrame({"D": ["13/02/2024", "01/01/2024"]})
    df1 = sync_datetime_columns(df1, x_field="D")
    assert df1[shadow_col_name("D")].iloc[0] == pd.Timestamp("2024-02-13")

    df2 = pd.DataFrame({"D": ["December 2024", "March 2025"]})
    df2 = sync_datetime_columns(df2, x_field="D")
    assert df2[shadow_col_name("D")].iloc[0] == pd.Timestamp("2024-12-01")


def test_only_x_field_is_attempted():
    """Only x_field gets a shadow column, even when another column parses as dates."""
    df = pd.DataFrame({
        "Month": ["December 2024", "March 2025"],
        "Series": ["2024-01-01", "2024-02-01"],  # date-like, but not the x-axis
    })
    df = sync_datetime_columns(df, x_field="Month")
    assert shadow_col_name("Month") in df.columns
    assert shadow_col_name("Series") not in df.columns


def test_missing_or_unknown_x_field_is_a_no_op():
    df = pd.DataFrame({"Month": ["December 2024", "March 2025"]})
    assert sync_datetime_columns(df, x_field=None) is df
    assert shadow_col_name("Month") not in df.columns

    df2 = pd.DataFrame({"Month": ["December 2024", "March 2025"]})
    df2 = sync_datetime_columns(df2, x_field="NotAColumn")
    assert shadow_col_name("Month") not in df2.columns


def test_iso_dates_are_never_read_day_first():
    # Monthly data on the 1st never has a day above 12, so a day-first parse would
    # succeed and turn March into 3 January.
    df = pd.DataFrame({
        "DATE": ["2020-12-01", "2021-03-01", "2021-12-01"],
        "Value": [1, 2, 3],
    })
    df = sync_datetime_columns(df, x_field="DATE")
    parsed = list(df[shadow_col_name("DATE")].dt.strftime("%Y-%m-%d"))
    assert parsed == ["2020-12-01", "2021-03-01", "2021-12-01"]


class TestStatedGranularity:
    """The date-voicing rules need the x column's granularity stated; only the
    spec or self-describing values state it."""

    def test_axis_format_states_it(self):
        from agent.date_cast import stated_granularity
        spec = {"encoding": {"x": {"field": "time", "type": "temporal", "axis": {"format": "%H:%M"}}}}
        assert stated_granularity(spec, "time", ["2026-05-06T16:00:00"])[0] == "minute"

    def test_time_unit_states_it(self):
        from agent.date_cast import stated_granularity
        spec = {"encoding": {"x": {"field": "d", "type": "temporal", "timeUnit": "yearmonth"}}}
        assert stated_granularity(spec, "d", ["2024-03-01"])[0] == "month"

    def test_self_describing_values(self):
        from agent.date_cast import stated_granularity
        assert stated_granularity(None, "year", [2019, 2020])[0] == "year"
        assert stated_granularity(None, "month", ["February 2025", "March 2025"])[0] == "month"

    def test_iso_dates_alone_state_nothing(self):
        # A column of "-01" days may be real days: never read granularity off the string.
        from agent.date_cast import stated_granularity
        assert stated_granularity(None, "d", ["2024-03-01", "2024-04-01"]) is None

    def test_another_fields_encoding_is_ignored(self):
        from agent.date_cast import stated_granularity
        spec = {"encoding": {"x": {"field": "other", "type": "temporal", "timeUnit": "year"}}}
        assert stated_granularity(spec, "d", ["2024-03-01"]) is None
