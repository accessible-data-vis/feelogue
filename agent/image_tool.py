"""
Chart image tool: a multimodal look at the chart as drawn, callable from inside
the data-query tool loop.

The image is the chart preview Unity sends with the chart (state["image_data"]).
It shows every series in its colour, including any the user has since hidden.
Its main job is resolving colour references ("the blue line") to a series name;
every value still comes from csv_query_tool.
"""
from typing import Annotated

from langchain.tools import tool
from langgraph.prebuilt import InjectedState

from .client import client
from .config import OPENAI_MODEL_IMAGE
from .prompts import IMAGE_TOOL_SYSTEM_PROMPT


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

    The image shows every series, including any the user has hidden. It says
    nothing about the tactile symbols the user feels on the display.

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
        base64_image = state.get("image_data")
        if not base64_image:
            return ("I don't have an image of the current chart to look at. "
                    "Answer from the data instead, or ask the user to describe "
                    "what they can see.")
        image_format = state.get("image_format") or "png"

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
