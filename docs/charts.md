# Adding a chart

A chart is a Vega-Lite spec in `interaction-manager/Assets/StreamingAssets/`, named
`compiled-vl-<dataName>-<chartType>-new.json`, with a preview PNG beside it. Unity finds
every spec in that folder (not its subfolders) when it starts.

## The spec

Everything Feelogue needs is standard Vega-Lite, apart from what Vega-Lite has no place
for, which goes in its `usermeta` block (custom data Vega-Lite passes on and ignores).

- **`usermeta`** (required): `dataName`, `chartType` and `displayName`. `displayName` is
  what the agent offers when asked to open a chart. Optional: `variant`, `previewImage`,
  and `textures` (below).
- **Marks**: `line`, `bar`, or `point` for a scatterplot. A `color` field makes a
  multi-series chart; for bars it makes a stacked bar, whose values must be zero or more.
- **How many fit**: line charts and bar charts show at most 7 points or bars; the rest are
  not drawn. Scatterplots show every point.
- **Symbols** (lines, scatterplots, when the chart loader's Use Series Symbols is on): a
  `shape` encoding on the series field picks each series' symbol. Vega-Lite `cross` is
  the plus, `diamond` the X (spoken "cross"), `triangle-up` and `triangle-down` the
  arrows, `circle` the single dot.
- **Textures** (bars, when the chart loader's Use Bar Textures is on): Vega-Lite has no
  channel for them, so `usermeta.textures` can name one per series, e.g.
  `{"Hardware": "checkerboard"}`, from solid, vertical stripes, checkerboard and hollow.
  Any series it doesn't name gets one by stack order, bottom first, in that list's
  order. Stack order is the colour scale's `domain` reversed, else reverse-alphabetical.
- **Point names**: fields listed in a `tooltip` encoding, other than the plotted ones,
  lead a point's spoken label ("Tesla Model 3, EV, 395, 73"). The agent sees every field
  either way.

## The presentation text

The spec carries no presentation text. When a chart loads, the agent works out the facts
from its data (trends, extremes, totals, crossings) and has the model put them into
words, one text per layer. Each layer has one job:

| Layer | On the display | Its job |
|---|---|---|
| title | the whole chart | What the chart is: subject, measure, span, chart type, the series |
| x axis | the x axis | What x is and its range; for bars, one bar per label and how each bar stacks |
| y axis | the y axis | What y is, its unit, range and tick step |
| each series | that series alone | How to recognise it (symbol or texture), then its own pattern with two anchor values |
| summary | the whole chart | Only what the whole chart adds: how the series compare (crossings, totals, groups), or for one series how much it changed overall and its biggest step |

Each layer is given only its own facts, so no layer repeats another. The text is made
once per chart while the agent runs. The title comes from `displayName`, and the axis
wording from the axis titles, so write those as you want them heard.

## The preview

When the agent needs to look at the chart, it draws it as the display shows it now. The
preview is the picture Unity sends with the chart, which the agent uses when it can't.
Draw it with the spec:

```bash
python interaction-manager/Assets/StreamingAssets/Tools/generate_chart_preview.py \
  --json-path <spec>.json --png-path chart-<chartType>-<dataName>-new.png
```

It is stamped with the spec it was drawn from, and `tests/test_chart_previews.py` fails
when a spec changes without its preview being redrawn. `tests/test_chart_specs.py` checks
each spec has its usermeta and no presentation text, and, when it sets symbols, one for
every series.
New files need a `.meta` file for Unity; opening the project in Unity makes one.
