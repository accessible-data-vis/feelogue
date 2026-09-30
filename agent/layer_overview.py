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


def _unit_formatter(y_title: str, y_field: str):
    """Format y values the way they should be spoken: '$1,100', '4.35%', '150 mm'."""
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

    if "$" in text or re.search(r"\b(AUD|USD|dollars?)\b", text, re.I):
        return lambda v: f"${number(v)}"
    if unit == "%":
        return lambda v: f"{number(v)}%"
    if unit:
        return lambda v: f"{number(v)} {unit}"
    return number


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


def _shape(values: list, xs: list, y_span: float) -> str:
    """Trend words computed from the numbers, so the model never judges them.
    Judged against the Y axis as drawn, because that is what the hand feels: a $3
    move is flat on an axis from $0 to $1,200 but fills an axis from $285 to $288."""
    steps = [b - a for a, b in zip(values, values[1:])]
    if not steps:
        return "has a single value"
    if y_span <= 0 or (max(values) - min(values)) / y_span < 0.05:
        return "stayed flat"
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
        start = next(i for i, s in enumerate(steps) if abs(s) >= abs(late) * 0.5)
        return f"{verb} sharply, speeding up from {xs[start]}"
    return f"{verb} sharply"


def _title(chart_name: str | None, measure: str, series_text: str | None, span: str, chart_phrase: str) -> str:
    """Built here, not by the model: '<name>: <measure> of <series>, <span>, <chart type>.'
    The display name contributes its subject and date, minus chart-type labels."""
    # After a name prefix the measure reads mid-sentence: "Average Price" -> "average
    # price", while acronyms such as GDP or USD keep their capitals.
    body = " ".join(w.lower() if w[:1].isupper() and w[1:].islower() else w
                    for w in measure.split()) if chart_name else measure
    if series_text:
        body += f" of {series_text}"
    title = f"{body}, {span}, {chart_phrase}."
    if chart_name:
        name = re.sub(r"\s*[-–:]?\s*(multi-series\s+)?(line|bar|stacked bar|scatter)\s*(chart|plot)?\s*$", "",
                      chart_name, flags=re.I).strip(" -–:")
        name = name.replace(" - ", ", ")
        if name:
            title = f"{name}: {title}"
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
    """Everything the layer text may say, derived from the data. None when the
    spec has no inline data to describe."""
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

    fmt_y = _unit_formatter(y_enc.get("title", ""), yf)
    all_y = [float(r[yf]) for r in rows if r.get(yf) is not None]
    y_domain = rendered.get("y_domain") or ((y_enc.get("scale") or {}).get("domain"))
    y_lo, y_hi = (y_domain if y_domain else [min(all_y), max(all_y)])[:2]
    y_span = float(y_hi) - float(y_lo)
    fmt_x = _x_formatter(x_enc)
    xs_raw = _ordered_x(list(dict.fromkeys(r[xf] for r in rows if xf in r)), x_enc.get("type"))
    xs = [fmt_x(x) for x in xs_raw]
    ordered_x = x_enc.get("type") in ("temporal", "ordinal")
    # Series names are always text, as in Unity and in the model's answer keys,
    # even when the data holds numbers such as years.
    names = list(dict.fromkeys(str(r[cf]) for r in rows if cf and cf in r)) if cf else []
    multi = bool(names)

    symbols = {str(s.get("name")): s.get("symbol") for s in (rendered.get("series") or []) if isinstance(s, dict)}

    def values_for(name):
        by_x = {r[xf]: float(r[yf]) for r in rows
                if (not multi or str(r.get(cf)) == name) and yf in r and r[yf] is not None}
        return [by_x.get(x) for x in xs_raw]

    series = {}
    for key in (names if multi else ["data"]):
        vals = values_for(key if multi else None)
        pairs = [(x, v) for x, v in zip(xs, vals) if v is not None]
        if not pairs:
            continue
        px, pv = [p[0] for p in pairs], [p[1] for p in pairs]
        entry = {
            "symbol": symbols.get(key),
            "start": [px[0], fmt_y(pv[0])], "end": [px[-1], fmt_y(pv[-1])],
            "highest": [px[pv.index(max(pv))], fmt_y(max(pv))],
            "lowest": [px[pv.index(min(pv))], fmt_y(min(pv))],
        }
        if ordered_x and chart_type != "point":
            entry["shape"] = _shape(pv, px, y_span)
        series[key] = entry

    crossings = []
    if multi and ordered_x and chart_type == "line":
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

    y_ticks = rendered.get("y_ticks") or ((y_enc.get("axis") or {}).get("values"))
    y_range = [fmt_y(y_lo), fmt_y(y_hi)]
    tick_step = fmt_y(y_ticks[1] - y_ticks[0]) if y_ticks and len(y_ticks) > 1 else None

    series_text = None
    if multi:
        series_text = names[0] if len(names) == 1 else ", ".join(names[:-1]) + " and " + names[-1]
    measure = re.sub(r"\s*\([^)]*\)", "", y_enc.get("title") or yf).strip()
    chart_phrase = _CHART_TYPE_PHRASES.get((chart_type, multi), f"{chart_type} chart")
    shown = rendered.get("points_shown")
    return {
        "layer_keys": ["title", "x_axis", "y_axis"] + list(series) + ["summary"],
        "title": _title((schema.get("metadata") or {}).get("displayName"), measure, series_text,
                        f"{xs[0]} to {xs[-1]}", chart_phrase),
        "x": {"title": x_enc.get("title") or xf, "first": xs[0], "last": xs[-1],
              # only when the window cuts some points off
              **({"points_total": len(xs), "shown_at_once": shown} if shown and shown < len(xs) else {})},
        "y": {"title": y_enc.get("title") or yf, "range": y_range, "tick_step": tick_step},
        "series": series,
        "crossings": crossings,
    }


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
    x, y, series = facts["x"], facts["y"], facts["series"]
    out = {"title": facts["title"]}

    x_text = f"The X axis shows {x['title']}, from {x['first']} to {x['last']}."
    if x.get("shown_at_once"):
        x_text += f" The display shows the first {x['shown_at_once']} of {x['points_total']} points."
    out["x_axis"] = x_text

    y_text = f"The Y axis shows {y['title']}, from {y['range'][0]} to {y['range'][1]}"
    out["y_axis"] = y_text + (f", with a tick every {y['tick_step']}." if y.get("tick_step") else ".")

    summary = []
    for key, s in series.items():
        name = "The data" if key == "data" else key
        symbol = f", shown with the {s['symbol']} symbol," if s.get("symbol") else ""
        shape = s.get("shape")
        if shape == "has a single value":
            text = f"{name}{symbol} has a single value: {s['start'][1]} at {s['start'][0]}."
        elif shape:
            text = (f"{name}{symbol} {shape}, from {s['start'][1]} at {s['start'][0]} "
                    f"to {s['end'][1]} at {s['end'][0]}.")
            summary.append(f"{name} {shape}")
        else:
            text = f"{name}{symbol} runs from {s['start'][1]} at {s['start'][0]} to {s['end'][1]} at {s['end'][0]}."
        out[key] = (text + f" Highest {s['highest'][1]} at {s['highest'][0]};"
                    f" lowest {s['lowest'][1]} at {s['lowest'][0]}.")

    for c in facts.get("crossings") or []:
        a, b = c["series"]
        where = f"at {c['at']}" if c["kind"] == "meet" else f"between {c['between'][0]} and {c['between'][1]}"
        summary.append(f"{a} and {b} {'meet' if c['kind'] == 'meet' else 'cross'} {where}")
    out["summary"] = ("; ".join(summary) + ".") if summary else "That covers the whole chart."
    return {k: out[k] for k in facts["layer_keys"]}

