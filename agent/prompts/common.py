"""Prompt fragments shared across intents: the derived date column rule, the
spoken-output voicing whitelist, and the stated date granularity."""
import json

import pandas as pd


def get_derived_date_column_rule(df):
    """Prompt rule for the `__dt__` shadow date column, or "" when there is none."""
    if not isinstance(df, pd.DataFrame):
        return ""
    from ..date_cast import shadow_columns
    original, shadow = shadow_columns(df)
    if not shadow:
        return ""
    return (
        f"\n- `{shadow}` is a derived datetime column parsed from "
        f"`{original}`. It exists only because `{original}` is stored as "
        "text, which sorts alphabetically instead of chronologically. "
        f"Every query involving order or time - sorting, min/max, "
        f"earliest/latest, neighbors, ordinals, ranges - must "
        f"compute on `{shadow}`, including where the query shapes above "
        f"say to sort or compare on `{original}`. But the query still "
        f"RETURNS `{original}` values, and every value you voice in a "
        f"message is an `{original}` value. Never put a `{shadow}` value "
        f"in an answer. `{shadow}` is not part of the user's data: do not "
        "count it when describing the columns, do not treat it as a "
        "series, and do not mention it to the user.\n"
    )


_CHARTER = """
**Voicing values and names (spoken output)**:
- Values should include their units and or what the x or y axis represents.
- All responses are spoken aloud by a TTS service. The listener has no
  written form to fall back on - what you write is exactly what they hear.
- DEFAULT: voice every dataset value and column name exactly as it appears
  in the tool result. Reformatting is the exception, allowed ONLY for the
  whitelisted cases below. If a value does not clearly match one of them,
  say it verbatim - an awkward reading is acceptable; an altered value is
  a factual error (Maxim of Quality).
- Reformatting never changes WHAT the value is, only how it is spoken.
  Never substitute, infer, complete, or "correct" any part of a value.
  If part of a value is unclear, voice that part as-is rather than guessing.

WHITELISTED reformatting:

- Units: appending the unit or currency that STRUCTURE states for a value's
  column (from the column name or axis title) is required context, not a
  reformatting of the value. This is the ONLY way a unit may be attached;
  a unit no permitted source states is never attached.
"""


_DATES = """
- Dates and times: dates are stored in ISO form, which pads every value out
  to full date-time precision. Padding is not data. Voice a date at the
  granularity STRUCTURE states for its column, NOT at the granularity the
  ISO string displays:
  - granularity year    "2025-01-01"          -> "2025"
  - granularity month   "2025-03-01"          -> "March 2025"
  - granularity day     "2025-03-15"          -> "15 March 2025"
  - granularity minute  "2025-03-15T14:30:00" -> "15 March 2025, two thirty pm"
  A value that is itself period-coded is voiced as that period:
  - "2025Q3" / "2025-Q3" -> "the third quarter of 2025" or when
  - granularity quarterly "2025-09-01" -> "September 2025"

- Granularity is stated, never inferred. If STRUCTURE states no granularity
  for the column, voice the value in full as written. Never read granularity
  off the shape of the string - a column of "-01" days may be real - and
  never name a period the data does not name: a March/June/September/December
  pattern is not licence to say "quarter", because whether that month opens
  or closes its quarter is a convention the data does not state.

- Times of day are spoken in words, 12-hour with am/pm, hour without a
  leading zero, minutes one through nine spoken as "oh {minute}":
  - "T14:30:00" -> "two thirty pm"
  - "T09:05:00" -> "nine oh five am"
  - "T15:00:00" -> "three pm" (on-the-hour times drop the minutes;
    never "three o'clock pm")
  - "T12:00:00" -> "midday", "T00:00:00" -> "midnight"
  - "T23:45:00" -> "eleven forty-five pm"
  Seconds are dropped when zero; a nonzero seconds value is data and is
  voiced after the time: "T14:30:45" -> "two thirty pm and forty-five
  seconds". Never "fourteen thirty and forty-five seconds".
  A time is voiced only when the column's stated granularity reaches it -
  on a day-granularity column the time component is padding and is dropped
  entirely, including a midnight that is written out in the string.

- Only reformat when the string parses completely and unambiguously as a
  date. Ambiguous forms ("03/04/2024", "1112024", bare numbers that might
  be years) are NOT dates for this purpose - voice them verbatim.

- Voicing always reads the ORIGINAL date column. A derived or shadow
  datetime column exists for sorting and computation only; its parsed form
  is never a source for spoken output, and the fact that a shadow column
  resolved an ambiguous format does not make the original unambiguous.

- Consecutive dates: several date values that run unbroken at the column's
  stated granularity may be voiced as ONE range instead of one value at a
  time:
  - "2020-01-01", "2020-02-01", "2020-03-01" (month) -> "January to March 2020"
  - "2018", "2019", "2020", "2021" (year)            -> "2018 to 2021"
  Both endpoints are included and each is voiced by the granularity rules
  above. Drop the shared year from the first endpoint only when both endpoints
  fall in the same year: "January to March 2020", but "November 2019 to March
  2020". Join with the spoken word "to" or "through", never a dash - the
  listener hears it read aloud.
  Permitted ONLY when all of these hold:
  - Granularity is STATED for the column. Without it you cannot know what the
    next value would be, so there is no run to recognise.
  - The run is GAPLESS: every value between the endpoints at that granularity
    is present in what you are voicing. "January to March 2020" asserts that
    February is there; if it is missing, the range is a factual error (Maxim
    of Quality). A set with a hole is voiced as the runs it actually contains:
    "January to March and May to July 2020".
  - The members share the ONE fact being reported - they all matched the
    filter, they are all above the threshold, they are the span the view
    covers. If each carries its own distinct value, a range drops the pairing:
    voice them individually, or summarize.
  - There are three or more of them. Two are voiced with "and" ("January and
    February 2020").
  Only dates collapse this way. A run of categories, IDs, codes, or plain
  numbers is voiced value by value however consecutive it looks.
  When the count matters to the answer, say it with the range ("all three
  months, January to March 2020") rather than making the listener count the
  span.
"""


_NUMBERS = """
- Numbers:
  - Digits are voiced exactly. You may insert thousands grouping for
    readability ("1234567" -> "1,234,567"), but do not truncate,
    re-scale, or drop digits. "1,234,567" is NOT "about 1.2 million".
    If a scale hint helps the listener, give the exact value first and
    mark the hint clearly: "1,234,567 - roughly 1.2 million".
  - Exactly is symmetric: never ADD digits either. The number of decimal
    places is part of the value and comes from the tool result, never
    from a convention about how numbers of that kind are written. Never
    pad to a fixed precision:
      "554250"  -> "554,250"    NEVER "554,250.00"
      "1.5"     -> "1.5"        NEVER "1.50"
      "12"      -> "12"         NEVER "12.0"
    A whole number stays whole. Padded decimals are heard as precision
    the data does not have, and a listener cannot see that the zeros
    were yours - so this is a factual error, exactly like dropping
    digits (Maxim of Quality).
  - A leading minus sign is voiced as "negative": "-4.2" -> "negative 4.2".
  - "%" -> "percent": "12.5%" -> "12.5 percent".
  - Scientific notation may be expanded mechanically:
    "1.2e6" -> "1.2 times 10 to the 6". Do not convert it to a plain
    number - that alters the stated precision.

- Currency:
  - Voice the currency by its most common spoken name, preferring the
    country-qualified form where that is how the currency is normally
    spoken: "JPY" -> "Japanese yen", "AUD" -> "Australian dollars",
    "USD" -> "US dollars", "GBP" -> "British pounds". Where the
    country-qualified form is not the natural spoken name, use the
    natural name: "EUR" -> "euros", "CHF" -> "Swiss francs",
    "INR" -> "Indian rupees". The name must identify the currency on
    its own - a bare "yen" or "dollars" is only acceptable when the
    currency itself is not stated in the data.
  - This full-name rule applies ONLY when STRUCTURE states the currency:
    an ISO 4217 code in the data, or a symbol whose currency a column
    name or axis title confirms (e.g. "$" under "Revenue (AUD)" ->
    "Australian dollars").
  - An unconfirmed symbol is voiced by its generic word only:
    "$" -> "dollars", "£" -> "pounds", "€" -> "euros". Never attach a
    country you inferred.
  - A currency amount is voiced with the name after the number, at the
    precision the DATA has. Naming a currency never changes the number:
    it does not license the two-decimal form money is conventionally
    written in.
      "554250" (confirmed AUD)  -> "554,250 Australian dollars"
                                   NEVER "554,250.00 Australian dollars"
      "$1,234.56" (confirmed AUD) -> "1,234 Australian dollars and 56
                                   cents"
    The "and X cents" form applies only to values that ALREADY carry
    exactly two decimals; any other precision is voiced as a decimal
    ("1.234 Australian dollars"), and a whole-number amount is voiced
    with no decimal part at all.
"""


_UNCERTAINTY = """
Uncertain axis, unit, or granularity meaning:
- "Certain" means STRUCTURE states it: the unit, currency, date granularity,
  or axis meaning is written in a column name, axis title, the stated column
  granularity, or the chart spec. If stated, use it. If NO permitted source
  states it, make NO adjustment of any kind - no expansion, no unit naming,
  no grouping beyond digits, no cents split, no dropping of date components.
  Voice the value verbatim and, if the user asks what it means, say the
  axis does not state it rather than guessing.
- This overrides every whitelist entry above. The whitelist grants
  permission to reformat; it never grants permission to guess.
- Example: magnitude suffixes in the data ("1.2M", "45k") are uncertain
  unless a column name or axis title defines them - "M" and "k" are not
  self-describing. Voice them verbatim.

NEVER reformat:
- Category values, IDs, codes, ticker symbols, postcodes - exact form may
  be meaningful.
- Numbers that might be identifiers (years, codes, phone-like strings,
  anything from an ID-like column) - no thousands grouping, no expansion.
- Anything you had to guess about to reformat.

Channel rule: this is presentation only. csv_query_tool queries always use
the exact raw names and values (Formulating a data query, rule 1).
"""


def get_spoken_format_whitelist() -> str:
    """The spoken-output formatting whitelist.

    Parts are concatenated rather than .format()-ed so that literal braces in
    the prompt body need no escaping.
    """
    return "".join([_CHARTER, _DATES, _NUMBERS, _UNCERTAINTY])


def get_date_granularity_rule(df, x_field: str | None, vega_lite_schema) -> str:
    """States the x column's date granularity, which the date-voicing rules
    need and never infer. Empty when nothing states it."""
    from ..date_cast import stated_granularity
    if not x_field:
        return ""
    spec = vega_lite_schema
    if isinstance(spec, str):
        try:
            spec = json.loads(spec) if spec else None
        except ValueError:
            spec = None
    values = []
    if isinstance(df, pd.DataFrame) and x_field in df.columns:
        values = df[x_field].dropna().unique().tolist()
    stated = stated_granularity(spec, x_field, values)
    if not stated:
        return ""
    granularity, source = stated
    return (f"\n- Stated date granularity (STRUCTURE): `{x_field}` is at {granularity} "
            f"granularity, as stated by {source}.\n")
