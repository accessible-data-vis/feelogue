"""
Layered-presentation text generated from a chart's own data, for charts whose spec
has no authored "overview" block (title, x_axis, y_axis, one entry per series or
"data", summary).

compute_facts derives everything checkable from the data and from what Unity drew
(each series' symbol, the Y range and ticks), including trend words, so the model
only phrases facts and never judges them. template_layer_overview says the same
facts in fixed sentences when the model's phrasing fails.
"""
import json
import re
import statistics
from datetime import datetime
from decimal import Decimal

from .client import client
from .config import OPENAI_MODEL
from .prompts import LAYER_OVERVIEW_SYSTEM_PROMPT

_CHART_TYPE_PHRASES = {
    ("line", False): "line chart", ("line", True): "multi-series line chart",
    ("bar", False): "bar chart", ("bar", True): "stacked bar chart",
    ("point", False): "scatterplot", ("point", True): "multi-series scatterplot",
}


def _unit_formatter(y_title: str, y_field: str, change: bool = False):
    """Format y values the way they should be spoken: '$1,100', '4.35%', '150 mm'.
    With change=True the value is a difference, and a change in a percentage is in
    percentage points."""
    text = f"{y_title or ''} {y_field or ''}"
    unit = None
    m = re.search(r"\(([^)]*)\)", y_title or "") or re.search(r"\(([^)]*)\)", y_field or "")
    if m:
        unit = m.group(1).strip()

    def number(v):
        # Values are spoken as the data has them, with thousands separators but no
        # rounding. round(v, 10) only clears float noise from computed tick steps.
        v = round(float(v), 10)
        if v.is_integer():
            return f"{v:,.0f}"
        return format(Decimal(repr(v)), ",f")

    scaled = re.fullmatch(r"\$\s*([KMB])", unit or "", re.I)   # "$K": thousands of dollars
    if scaled:
        word = {"K": "thousand", "M": "million", "B": "billion"}[scaled.group(1).upper()]
        return lambda v: f"{number(v)} {word} dollars"
    if "$" in text or re.search(r"\b(AUD|USD|dollars?)\b", text, re.I):
        return lambda v: f"${number(v)}"
    if unit == "%":
        return (lambda v: f"{number(v)} percentage points") if change else (lambda v: f"{number(v)}%")
    if unit:
        return lambda v: f"{number(v)} {unit}"
    return number


def _lower_words(text: str) -> str:
    """'Average Price' -> 'average price', keeping acronyms such as CO2 or GDP."""
    return " ".join(w.lower() if w[:1].isupper() and w[1:].islower() else w for w in text.split())


def _join(names: list) -> str:
    return names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]


def _plain_number(v) -> str:
    v = round(float(v), 10)
    return str(int(v)) if v.is_integer() else format(Decimal(repr(v)), "f")


def _x_formatter(x_enc: dict):
    """Speak x values as the axis shows them (e.g. '%H:%M' -> '16:00')."""
    fmt = ((x_enc or {}).get("axis") or {}).get("format")
    if not fmt or (x_enc or {}).get("type") != "temporal":
        return str

    def fmt_x(v):
        try:
            return datetime.fromisoformat(str(v)).strftime(fmt)
        except ValueError:
            return str(v)
    return fmt_x


def _at(label: str) -> str:
    """'at 17:00' for a time of day, 'in 2021' or 'in Q3' otherwise."""
    return f"at {label}" if ":" in str(label) else f"in {label}"


def _shape(values: list, xs: list, y_span: float, fmt=None) -> str:
    """Trend words computed from the numbers, so the model never judges them.
    Judged against the Y axis as drawn, because that is what the hand feels: a $3
    move is flat on an axis from $0 to $1,200 but fills an axis from $285 to $288."""
    steps = [b - a for a, b in zip(values, values[1:])]
    if not steps:
        return "has a single value"
    if y_span <= 0 or (max(values) - min(values)) / y_span < 0.05:
        return "stayed flat"
    # A dip or a peak in the middle that the series turns back from by more than a
    # tenth of the axis on both sides.
    turns = []
    for i, kind in ((values.index(min(values)), "low"), (values.index(max(values)), "peak")):
        if 0 < i < len(values) - 1:
            turns.append((min(abs(values[i] - values[0]), abs(values[-1] - values[i])), i, kind))
    if turns and max(turns)[0] > 0.1 * y_span:
        _, i, kind = max(turns)
        at = f"of {fmt(values[i])} {_at(xs[i])}" if fmt else _at(xs[i])
        return f"fell to a low {at}, then rose" if kind == "low" else f"rose to a peak {at}, then fell"
    change = values[-1] - values[0]
    verb = "rose" if change > 0 else "fell"
    if abs(change) <= 0.5 * y_span:
        monotonic = all(s >= 0 for s in steps) or all(s <= 0 for s in steps)
        return f"{verb} {'steadily' if monotonic else 'unevenly'}"
    if len(steps) < 2:
        return f"{verb} sharply"   # one step: no early and late halves to compare
    half = len(steps) // 2
    early, late = statistics.fmean(steps[:half]), statistics.fmean(steps[half:])
    if abs(late) > 2.5 * max(abs(early), 1e-9):
        start = next((i for i, s in enumerate(steps) if (s > 0) == (change > 0) and abs(s) >= abs(late) * 0.5), None)
        if start is not None:
            return f"{verb} sharply, faster after {xs[start]}"
    return f"{verb} sharply"


def _relationship(xs: list, ys: list) -> str:
    """How y moves as x increases, worded from the correlation (the same words the
    data-query prompt uses for a scatterplot trend)."""
    if len(xs) < 3:
        return "has too few points for a pattern"
    try:
        r = statistics.correlation(xs, ys)
    except statistics.StatisticsError:   # every x, or every y, the same
        return "shows no clear pattern"
    if r >= 0.8:
        return "rises steadily"
    if r >= 0.5:
        return "tends to rise"
    if r > -0.5:
        return "shows no clear pattern"
    if r > -0.8:
        return "tends to fall"
    return "falls steadily"


_RISING = ("rises steadily", "tends to rise")
_FALLING = ("falls steadily", "tends to fall")


def _steps(values: list, xs: list, fmt_change) -> dict:
    """A series' overall change and its biggest single rise and fall, read in x order."""
    delta = values[-1] - values[0]
    out = {"change": {"direction": "higher" if delta > 0 else "lower" if delta < 0 else "level",
                      "by": fmt_change(abs(delta)), "from": xs[0], "to": xs[-1]}}
    steps = [(b - a, xs[i], xs[i + 1]) for i, (a, b) in enumerate(zip(values, values[1:]))]
    if steps:
        rise, fall = max(steps), min(steps)
        if rise[0] > 0:
            out["biggest_rise"] = {"by": fmt_change(rise[0]), "from": rise[1], "to": rise[2]}
        if fall[0] < 0:
            out["biggest_fall"] = {"by": fmt_change(-fall[0]), "from": fall[1], "to": fall[2]}
    return out


def _stack_order(color: dict | None, names: list) -> list:
    """Bottom to top, as Unity stacks the bars: the colour domain reversed, else
    reverse-alphabetical."""
    domain = [str(d) for d in (((color or {}).get("scale") or {}).get("domain") or [])]
    ordered = [d for d in reversed(domain) if d in names]
    return ordered + sorted((n for n in names if n not in ordered), reverse=True)


_TEXTURE_OPENERS = {"solid": "Shown solid", "vertical stripes": "Shown with vertical stripes",
                    "checkerboard": "Shown with a checkerboard texture", "hollow": "Shown hollow"}


def _title(chart_name: str | None, measure: str, series_text: str | None, span: str | None, chart_phrase: str) -> str:
    """Built here, not by the model: '<name>: <measure> of <series>, <span>, <chart type>.'
    The display name contributes its subject and date, minus chart-type labels, and the
    measure is left out when the name already says it."""
    name = ""
    if chart_name:
        name = re.sub(r"\s*[-–:]?\s*(multi-series\s+)?(line|bar|stacked bar|scatter)\s*(chart|plot)?\s*$", "",
                      chart_name, flags=re.I).strip(" -–:").replace(" - ", ", ")
    if name and measure.lower() in name.lower():
        body = series_text or ""
    else:
        # After a name prefix the measure reads mid-sentence: "Average Price" -> "average
        # price", while acronyms such as GDP or USD keep their capitals.
        body = _lower_words(measure) if name else measure
        if series_text:
            body += f" of {series_text}"
    title = ", ".join(p for p in (body, span, chart_phrase) if p) + "."
    if name:
        title = f"{name}: {title}" if body else f"{name}, {title}"
    return title


def _ordered_x(xs: list, x_type: str | None) -> list:
    """X values in axis order: numbers by value, dates by time, categories in the spec's order."""
    if x_type not in ("quantitative", "temporal"):
        return xs

    def key(v):
        if isinstance(v, (int, float)) or x_type == "quantitative":
            return float(v)
        return datetime.fromisoformat(str(v)).replace(tzinfo=None)

    try:
        return sorted(xs, key=key)
    except (ValueError, TypeError):
        return xs


def compute_facts(schema: dict, rendered: dict | None) -> dict | None:
    """Everything the layer text may say, derived from the data, grouped by the layer
    that says it: "x" and "y" for the axes, "series" for each series layer, and
    "summary" for what only the whole chart shows. None when the spec has no inline
    data to describe."""
    enc = schema.get("encoding") or {}
    rows = ((schema.get("data") or {}).get("values")) or []
    x_enc, y_enc = enc.get("x") or {}, enc.get("y") or {}
    xf, yf = x_enc.get("field"), y_enc.get("field")
    color = enc.get("color")
    cf = color.get("field") if isinstance(color, dict) else None
    if not rows or not xf or not yf:
        return None
    rendered = rendered or {}
    mark = schema.get("mark")
    chart_type = rendered.get("chart_type") or (mark.get("type") if isinstance(mark, dict) else mark) or "point"
    scatter = chart_type == "point" and x_enc.get("type") == "quantitative"

    fmt_y = _unit_formatter(y_enc.get("title", ""), yf)
    fmt_change = _unit_formatter(y_enc.get("title", ""), yf, change=True)
    all_y = [float(r[yf]) for r in rows if r.get(yf) is not None]
    if not all_y:
        return None
    y_domain = rendered.get("y_domain") or ((y_enc.get("scale") or {}).get("domain"))
    y_lo, y_hi = (y_domain if y_domain else [min(all_y), max(all_y)])[:2]
    y_span = float(y_hi) - float(y_lo)
    fmt_x = _x_formatter(x_enc)
    # A scatterplot's x values are numbers: with their unit when the axis title gives
    # one, and without thousands separators otherwise (a year stays "2019").
    fmt_xq = (_unit_formatter(x_enc.get("title", ""), xf)
              if re.search(r"\(([^)]*)\)", x_enc.get("title") or "") else _plain_number)
    xs_raw = _ordered_x(list(dict.fromkeys(r[xf] for r in rows if xf in r)), x_enc.get("type"))
    xs = [fmt_x(x) for x in xs_raw]
    ordered_x = x_enc.get("type") in ("temporal", "ordinal")
    # Series names are always text, as in Unity and in the model's answer keys,
    # even when the data holds numbers such as years.
    names = list(dict.fromkeys(str(r[cf]) for r in rows if cf and cf in r)) if cf else []
    multi = bool(names)

    marks = {str(s.get("name")): s for s in (rendered.get("series") or []) if isinstance(s, dict)}
    # A field the tooltip names besides the plotted ones names each point (a car model).
    tooltip = enc.get("tooltip") or []
    name_field = next((t.get("field") for t in (tooltip if isinstance(tooltip, list) else [tooltip])
                       if isinstance(t, dict) and t.get("field") not in (xf, yf, cf)), None)

    def values_for(name):
        by_x = {r[xf]: float(r[yf]) for r in rows
                if (not multi or str(r.get(cf)) == name) and yf in r and r[yf] is not None}
        return [by_x.get(x) for x in xs_raw]

    def points_of(name):
        """A scatterplot series' points as (x, y, point name), by x."""
        pts = [(float(r[xf]), float(r[yf]), r.get(name_field) if name_field else None)
               for r in rows if (name is None or str(r.get(cf)) == name)
               and r.get(xf) is not None and r.get(yf) is not None]
        return sorted(pts, key=lambda p: (p[0], p[1]))

    def named_point(p):
        """A point as one phrase, so its x and y can't be paired with another's."""
        values = f"{fmt_xq(p[0])}, {fmt_y(p[1])}"
        return f"{p[2]} ({values})" if p[2] else values

    series, relationship = {}, {}
    for key in (names if multi else ["data"]):
        mark = marks.get(key) or {}
        entry = {"symbol": mark.get("symbol")}
        # How to recognise the series once the others are back: its symbol, or for a
        # stacked bar its texture.
        if mark.get("symbol"):
            entry["opener"] = f"Plotted using the {mark['symbol']} symbol"
        elif multi and mark.get("texture") in _TEXTURE_OPENERS:
            entry["texture"] = mark["texture"]
            entry["opener"] = _TEXTURE_OPENERS[mark["texture"]]

        if scatter:
            pts = points_of(key if multi else None)
            if not pts:
                continue
            ys = [p[1] for p in pts]
            relationship[key] = _relationship([p[0] for p in pts], ys)
            entry.update({
                "sits": (f"between {fmt_y(min(ys))} and {fmt_y(max(ys))}, "
                         f"from {fmt_xq(pts[0][0])} to {fmt_xq(pts[-1][0])}"),
                # with its least and greatest x points, in one phrase so none can be paired wrongly
                "relationship": (f"{relationship[key]}, from {named_point(pts[0])} "
                                 f"to {named_point(pts[-1])}"),
            })
            series[key] = entry
            continue

        vals = values_for(key if multi else None)
        pairs = [(x, v) for x, v in zip(xs, vals) if v is not None]
        if not pairs:
            continue
        px, pv = [p[0] for p in pairs], [p[1] for p in pairs]
        hi, lo_ = pv.index(max(pv)), pv.index(min(pv))
        if ordered_x:
            # Start and end, plus a peak or low only when it falls in between: one at
            # either end is already said.
            shape = _shape(pv, px, y_span, fmt_y)
            entry.update({"shape": shape, "start": [px[0], fmt_y(pv[0])], "end": [px[-1], fmt_y(pv[-1])]})
            # Not for a flat series, or a turn its shape already names.
            if 0 < hi < len(pv) - 1 and shape != "stayed flat" and "peak" not in shape:
                entry["peak"] = [px[hi], fmt_y(pv[hi])]
            if 0 < lo_ < len(pv) - 1 and shape != "stayed flat" and "low" not in shape:
                entry["low"] = [px[lo_], fmt_y(pv[lo_])]
        else:
            # Categories have no start or end, only a highest and lowest.
            entry.update({"highest": [px[hi], fmt_y(pv[hi])], "lowest": [px[lo_], fmt_y(pv[lo_])]})
        series[key] = entry

    # What only the whole chart shows: the summary layer says these and nothing else.
    summary = {}
    if scatter and multi:
        def mean(name, i):
            return statistics.fmean(p[i] for p in points_of(name))
        everything = points_of(None)
        by_y = sorted(series, key=lambda n: mean(n, 1))
        by_x = sorted(series, key=lambda n: mean(n, 0))
        summary.update({"lowest_group": by_y[0], "highest_group": by_y[-1],
                        "leftmost_group": by_x[0], "rightmost_group": by_x[-1]})
        whole_chart = _relationship([p[0] for p in everything], [p[1] for p in everything])
        rising = [n for n in series if relationship[n] in _RISING]
        falling = [n for n in series if relationship[n] in _FALLING]
        x_measure = re.sub(r"\s*\([^)]*\)", "", x_enc.get("title") or xf).strip().lower()
        for groups, verb, same in ((rising, "rise", _RISING), (falling, "fall", _FALLING)):
            if groups and not (falling if verb == "rise" else rising) and whole_chart not in same:
                summary["contrast"] = (f"{_join(groups)} {verb if len(groups) > 1 else verb + 's'} as "
                                       f"{x_measure} increases, but the chart as a whole {whole_chart}")
    elif chart_type == "bar" and multi:
        # Totals as drawn: segments stack by size.
        by_name = {n: values_for(n) for n in names}
        totals = [sum(abs(by_name[n][i] or 0) for n in names) for i in range(len(xs_raw))]
        summary["highest_total"] = [xs[totals.index(max(totals))], fmt_y(max(totals))]
        summary["lowest_total"] = [xs[totals.index(min(totals))], fmt_y(min(totals))]
        if ordered_x:
            summary["totals_shape"] = _shape(totals, xs, y_span)
        sums = {n: sum(abs(v) for v in by_name[n] if v is not None) for n in names}
        largest = max(sums, key=sums.get)
        every_bar = all(max(names, key=lambda n: abs(by_name[n][i] or 0)) == largest
                        for i in range(len(xs_raw)))
        summary["largest_series"] = largest + (", the largest in every bar" if every_bar else "")
    elif multi:
        if ordered_x:
            summary["shapes"] = {n: s["shape"] for n, s in series.items() if "shape" in s}
        summary["crossings"] = _crossings(names, values_for, xs, fmt_y) if ordered_x and chart_type == "line" else []
    elif not scatter and "data" in series:
        only = series["data"]
        pairs = [(x, v) for x, v in zip(xs, values_for(None)) if v is not None]
        vals = [v for _, v in pairs]
        if ordered_x:
            summary.update(_steps(vals, [x for x, _ in pairs], fmt_change))
        else:
            summary["difference"] = {"highest": only["highest"][0], "lowest": only["lowest"][0],
                                     "by": fmt_change(max(vals) - min(vals))}

    x_first, x_last = xs[0], xs[-1]
    if scatter:
        x_domain = (x_enc.get("scale") or {}).get("domain")
        lo, hi = (x_domain[:2] if x_domain else [xs_raw[0], xs_raw[-1]])
        x_first, x_last = fmt_xq(float(lo)), fmt_xq(float(hi))

    y_ticks = rendered.get("y_ticks") or ((y_enc.get("axis") or {}).get("values"))
    tick_step = fmt_y(y_ticks[1] - y_ticks[0]) if y_ticks and len(y_ticks) > 1 else None

    series_text = _join(names) if multi else None
    measure = re.sub(r"\s*\([^)]*\)", "", y_enc.get("title") or yf).strip()
    chart_phrase = _CHART_TYPE_PHRASES.get((chart_type, multi), f"{chart_type} chart")
    shown = rendered.get("points_shown")
    x_facts = {"title": x_enc.get("title") or xf, "first": x_first, "last": x_last}
    if chart_type == "bar":
        x_facts["mark"] = "bar"
        if multi:
            x_facts["stack_order"] = _stack_order(color, names)
    if shown and shown < len(xs):   # only when the window cuts some points off
        x_facts.update({"points_total": len(xs), "shown_at_once": shown})
    return {
        "layer_keys": ["title", "x_axis", "y_axis"] + list(series) + ["summary"],
        # A scatterplot's x is a measure, not a span: no span in its title.
        "title": _title((schema.get("usermeta") or {}).get("displayName"), measure, series_text,
                        None if scatter else f"{xs[0]} to {xs[-1]}", chart_phrase),
        "x": x_facts,
        "y": {"title": y_enc.get("title") or yf, "measure": _lower_words(measure),
              "range": [fmt_y(y_lo), fmt_y(y_hi)], "tick_step": tick_step},
        "series": series,
        "summary": summary,
    }


def _crossings(names: list, values_for, xs: list, fmt_y) -> list:
    """Where two lines meet or cross, and whether one then pulls away."""
    crossings = []
    for i, a in enumerate(names):
        for b in names[i + 1:]:
            va, vb = values_for(a), values_for(b)
            d = [(p - q) if p is not None and q is not None else None for p, q in zip(va, vb)]
            for k, dk in enumerate(d):
                if dk is None:
                    continue
                prev = d[k - 1] if k else None
                if dk == 0:
                    c = {"series": [a, b], "at": xs[k], "value": fmt_y(va[k]), "kind": "meet"}
                elif prev not in (None, 0) and (prev > 0) != (dk > 0):
                    c = {"series": [a, b], "between": [xs[k - 1], xs[k]], "kind": "cross"}
                else:
                    continue
                after = [g for g in d[k:] if g is not None]
                if len(after) > 1 and all(abs(g2) > abs(g1) for g1, g2 in zip(after, after[1:])):
                    leader = a if after[-1] > 0 else b
                    other = b if leader == a else a
                    c["afterwards"] = f"{leader} pulls away from {other}"
                crossings.append(c)
    return crossings


def generate_layer_overview(facts: dict) -> dict | None:
    """Phrase the facts as one text per layer. Returns {layer key: text}, or None
    if the model's answer is missing any layer."""
    resp = client.chat.completions.create(
        model=OPENAI_MODEL,
        temperature=0,
        response_format={"type": "json_object"},
        messages=[
            {"role": "system", "content": LAYER_OVERVIEW_SYSTEM_PROMPT},
            {"role": "user", "content": "Chart facts:\n" + json.dumps(facts, indent=1)},
        ],
    )
    out = json.loads(resp.choices[0].message.content or "{}")
    out["title"] = facts["title"]
    keys = facts["layer_keys"]
    if not all(isinstance(out.get(k), str) and out[k].strip() for k in keys):
        print(f"[layer_overview] incomplete answer, keys={list(out)}")
        return None
    return {k: out[k].strip() for k in keys}


def template_layer_overview(facts: dict) -> dict:
    """The same facts in fixed sentences, for when the model's phrasing fails."""
    x, y, series, summary = facts["x"], facts["y"], facts["series"], facts.get("summary") or {}
    out = {"title": facts["title"]}

    x_text = f"The X axis shows {x['title']}, from {x['first']} to {x['last']}"
    x_text += ", one bar each." if x.get("mark") == "bar" else "."
    if x.get("stack_order"):
        x_text += f" Each bar stacks {', then '.join(x['stack_order'])}, bottom first."
    if x.get("shown_at_once"):
        x_text += f" The display shows the first {x['shown_at_once']} of {x['points_total']} points."
    out["x_axis"] = x_text

    y_text = f"The Y axis shows {y['title']}, from {y['range'][0]} to {y['range'][1]}"
    out["y_axis"] = y_text + (f", with a tick every {y['tick_step']}." if y.get("tick_step") else ".")

    for key, s in series.items():
        name = "The data" if key == "data" else key
        if s.get("symbol"):
            drawn = f", shown with the {s['symbol']} symbol,"
        elif s.get("opener"):
            drawn = f", {s['opener'][0].lower()}{s['opener'][1:]},"
        else:
            drawn = ""
        if "relationship" in s:
            out[key] = f"{name}{drawn} sits {s['sits']}, and {s['relationship']}."
            continue
        shape = s.get("shape")
        if shape == "has a single value":
            out[key] = f"{name}{drawn} has a single value: {s['start'][1]} at {s['start'][0]}."
        elif shape:
            text = (f"{name}{drawn} {shape}, from {s['start'][1]} at {s['start'][0]} "
                    f"to {s['end'][1]} at {s['end'][0]}.")
            if s.get("peak"):
                text += f" Its peak is {s['peak'][1]} at {s['peak'][0]}."
            if s.get("low"):
                text += f" Its low is {s['low'][1]} at {s['low'][0]}."
            out[key] = text
        else:
            out[key] = (f"{name}{drawn} is highest at {s['highest'][0]}, {s['highest'][1]}, "
                        f"and lowest at {s['lowest'][0]}, {s['lowest'][1]}.")

    # The summary says only what the whole chart adds.
    parts = []
    if summary.get("highest_total"):
        hi, lo_ = summary["highest_total"], summary["lowest_total"]
        parts.append(f"Totals are highest at {hi[0]}, {hi[1]}, and lowest at {lo_[0]}, {lo_[1]}")
    if summary.get("largest_series"):
        parts.append(f"the largest series is {summary['largest_series']}")
    if summary.get("lowest_group"):
        parts.append(f"{summary['lowest_group']} sits lowest and {summary['highest_group']} highest; "
                     f"{summary['leftmost_group']} sits furthest left and {summary['rightmost_group']} furthest right")
    if summary.get("contrast"):
        parts.append(summary["contrast"])
    for name, shape in (summary.get("shapes") or {}).items():
        parts.append(f"{name} {shape}")
    for c in summary.get("crossings") or []:
        a, b = c["series"]
        where = f"at {c['at']}" if c["kind"] == "meet" else f"between {c['between'][0]} and {c['between'][1]}"
        parts.append(f"{a} and {b} {'meet' if c['kind'] == 'meet' else 'cross'} {where}")
    if summary.get("change"):
        ch = summary["change"]
        parts.append("The data ends where it began" if ch["direction"] == "level"
                     else f"The data ends {ch['by']} {ch['direction']} than it began")
        for kind in ("rise", "fall"):
            step = summary.get(f"biggest_{kind}")
            if step:
                parts.append(f"the biggest {kind} was {step['by']}, from {step['from']} to {step['to']}")
    if summary.get("difference"):
        d = summary["difference"]
        parts.append(f"{d['highest']} is {d['by']} above {d['lowest']}")
    out["summary"] = ("; ".join(parts) + ".") if parts else "That is the whole chart."
    return {k: out[k] for k in facts["layer_keys"]}
