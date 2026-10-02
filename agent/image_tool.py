"""
Chart image tool: a multimodal look at the chart as drawn, callable from inside
the data-query tool loop.

The image is the chart as the display shows it now, drawn from the loaded spec:
only the points the display has room for, and anything the display isn't showing
(series the user hid, or the other series during the presentation) drawn faint.
When that can't be drawn it falls back to the preview Unity sends with the chart
(state["image_data"]). Its main job is resolving colour references ("the blue
line") to a series name; every value still comes from csv_query_tool.
"""
import base64
import copy
import json
from typing import Annotated

from langchain.tools import tool
from langgraph.prebuilt import InjectedState

from .client import client
from .config import OPENAI_MODEL_IMAGE
from .context import get_chart_spec
from .prompts import IMAGE_TOOL_SYSTEM_PROMPT

MAX_MARKS_SHOWN = 7     # line points or bars the display draws (Unity's VegaChartLoader)
FAINT = 0.15            # opacity of what the display isn't showing


def current_view_spec(spec: dict, hidden_series: list | None = None, presentation: dict | None = None) -> dict:
    """The spec redrawn as the display shows it now. Lines and bars keep their first
    MAX_MARKS_SHOWN x values. Faint: during the presentation (which pauses the filter)
    every other series on a series layer and all the data on an axis layer; otherwise
    the series the user hid. Each series keeps its colour from the full chart."""
    view = copy.deepcopy(spec)
    view.pop("overview", None)
    enc = view.setdefault("encoding", {})
    xf = (enc.get("x") or {}).get("field")
    color = enc.get("color") if isinstance(enc.get("color"), dict) else {}
    cf = color.get("field")
    mark = view.get("mark")
    mark_type = mark.get("type") if isinstance(mark, dict) else mark
    all_rows = ((spec.get("data") or {}).get("values")) or []
    rows = list(all_rows)

    if mark_type in ("line", "bar") and xf:
        shown = list(dict.fromkeys(str(r.get(xf)) for r in rows))[:MAX_MARKS_SHOWN]
        rows = [r for r in rows if str(r.get(xf)) in shown]
    view.setdefault("data", {})["values"] = rows

    names = list(dict.fromkeys(str(r[cf]) for r in all_rows if cf in r)) if cf else []
    if cf:
        # Vega-Lite gives colours (and shapes) to the series it is drawing, so pin
        # them to the full chart's: hiding one must not recolour the rest.
        for channel in ("color", "shape"):
            ch = enc.get(channel)
            if not isinstance(ch, dict) or ch.get("field") != cf or "domain" in (ch.get("scale") or {}):
                continue
            if "sort" not in ch:
                domain = sorted(names)          # Vega-Lite's default order
            elif ch["sort"] is None:
                domain = names
            else:
                continue
            ch.setdefault("scale", {})["domain"] = domain

    layer = (presentation or {}).get("layer")
    if layer in ("x_axis", "y_axis"):
        enc["opacity"] = {"value": FAINT}
        return view
    if layer == "series" and cf:
        faint = [n for n in names if n != str(presentation.get("series"))]
    elif not presentation and cf and hidden_series:
        faint = [str(s) for s in hidden_series]
    else:
        faint = []
    if faint:
        enc["opacity"] = {"condition": {"test": f"indexof({json.dumps(faint)}, datum[{json.dumps(cf)}]) >= 0",
                                        "value": FAINT},
                          "value": 1}
    return view


# The last view drawn, as (spec, view, PNG): reused until the chart or its view changes,
# so several image questions in one answer draw it once.
_last_render: tuple | None = None


def render_current_view(state: dict) -> str | None:
    """The current view as a base64 PNG, or None when it can't be drawn."""
    global _last_render
    spec = get_chart_spec()
    if not spec:
        return None
    view_key = (tuple(str(s) for s in state.get("hidden_series") or []),
                json.dumps(state.get("presentation"), sort_keys=True))
    if _last_render and _last_render[0] is spec and _last_render[1] == view_key:
        return _last_render[2]
    try:
        import vl_convert
        view = current_view_spec(spec, state.get("hidden_series"), state.get("presentation"))
        png = base64.b64encode(vl_convert.vegalite_to_png(json.dumps(view), scale=2)).decode("ascii")
    except Exception as e:
        print(f"chart_image_tool: current view not drawn ({type(e).__name__}: {e}); using the preview")
        return None
    _last_render = (spec, view_key, png)
    return png


@tool
def chart_image_tool(
    question: str,
    state: Annotated[dict, InjectedState],
) -> str:
    """
    Look at the chart AS IT IS DRAWN and answer one question about its appearance.

    Its job is almost always COLOUR: series are told apart on screen by colour and
    nothing else, and the data cannot tell you which series is drawn in which one.
    When the user refers to a series by colour ("the blue line", "the orange one"),
    this tool is the only way to turn that into a real series name you can then use
    everywhere else. It also answers how the legend and axes are labelled.

    The image is the chart as the display shows it now. Series the display isn't
    showing (hidden by the user, or other series during the presentation) are
    drawn faint, so their colours can still be read. It says nothing about the
    tactile symbols the user feels on the display.

    Do NOT use it for anything the data can answer: values, averages, totals,
    extents, which series is higher, and where series cross are all csv_query_tool's,
    which computes them exactly. This tool is instructed to refuse them.

    Args:
        question: One specific, self-contained question about the chart's
            appearance. Name what you are asking about the way the user did
            ("which series is the blue line?"). Ask for one thing per call.

    Returns:
        A short factual description of what is visible, naming elements as they
        appear in the legend or axis labels. When the image cannot settle the
        question, a sentence beginning "Cannot be determined from the image:".
        If no chart image is available at all, a plain-language message saying so.
    """
    print("Chart Image Question: \n", question)
    try:
        base64_image, image_format = render_current_view(state), "png"
        if not base64_image:
            base64_image, image_format = state.get("image_data"), state.get("image_format") or "png"
        if not base64_image:
            return ("I don't have an image of the current chart to look at. "
                    "Answer from the data instead, or ask the user to describe "
                    "what they can see.")

        response = client.responses.create(
            model=OPENAI_MODEL_IMAGE,
            instructions=IMAGE_TOOL_SYSTEM_PROMPT,
            input=[
                {
                    "role": "user",
                    "content": [
                        {"type": "input_text", "text": question},
                        {
                            "type": "input_image",
                            "image_url": f"data:image/{image_format};base64,{base64_image}",
                            "detail": "auto",
                        },
                    ],
                },
            ],
        )
        result = response.output_text
        print("Chart Image Answer: \n", result)
        return result or "I couldn't get a reading of the chart image for that question."

    except Exception as e:
        print(f"chart_image_tool error: {type(e).__name__}: {e}")
        return f"I encountered an error while looking at the chart image: {str(e)}"
