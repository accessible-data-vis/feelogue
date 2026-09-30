# Unity and agent messages

Unity and the Python agent talk over MQTT with JSON messages. Unity publishes to
`agent_in` and listens on `agent_out`; the agent does the opposite (topics are set
in `.env` as `MQTT_TOPIC_IN` / `MQTT_TOPIC_OUT`, and in `MQTTReceiver`'s Inspector).

The agent tells messages apart by their top-level key (or `message_type`).
Fields marked *optional* may be missing; everything else is always sent.
`tests/test_message_contract.py` checks the agent's side against this file.

## Unity to agent (`agent_in`)

### `chart_metadata_index`
Sent when MQTT connects. The catalogue the agent uses to resolve "open the X chart".

| field | meaning |
|---|---|
| `chart_count` | number of charts |
| `charts` | list of `{chart_id, data_name, chart_type, columns, chart_name}` |

### `rtd_data_for_agent`
Sent on every chart load, including reloading the same chart. The agent treats it as
a load: it resets its per-chart state and the user's filter, and any request pieces
held for this load (see below) run when the next `layer_data_update` arrives.

| field | meaning |
|---|---|
| `chart_type` | `line`, `bar` or `point` |
| `data_name` | the chart's data name |
| `schema` | the Vega-Lite spec as loaded; its `overview` block, if any, is the authored presentation text |
| `rendered` | what the display actually draws: `{chart_type, series: [{name, symbol}] or null, y_domain, y_ticks, points_shown, points_total}` |
| `image_data`, `image_format` | *optional*: the chart's preview PNG, base64 |

### `layer_data_update` (`message_type`)
Sent after a load and on every redraw. Replaces the agent's data frame.

| field | meaning |
|---|---|
| `layer_name` | the data name |
| `chart_type` | as above |
| `x_field`, `y_field` | the chart's x and y fields |
| `series_field` | *optional*: the colour field on multi-series charts |
| `data_count` | number of rows |
| `data` | every row: the chart's fields, `_id` (row id) and `in_view` (drawn on the display now) |

While the presentation runs, `in_view` ignores the series it isolates: the agent
sees the whole chart.

### `user_request_for_agent`
One spoken or typed question.

| field | meaning |
|---|---|
| `transcript` | `{text_transcript, confidence, words}` |
| `touchdata` | `{left_touch, right_touch}`: each is `"No touch"` or the last double tap, `{node_count, nodes: {<node id>: {node_xy, node_values, probability}}, touch_timestamp}` |
| `highlighted_context` | `"No highlight"` or the navigated point, `{node_count, nodes: {<node id>: {node_xy, node_values, probability, source}}, highlight_timestamp}` |
| `presentation` | `null`, or the layer on the display: `{layer: title \| x_axis \| y_axis \| series \| data \| summary, series}` (`series` only on a series layer) |

### `chart_details` (`message_type`)
Handled by the agent (it stores the image) but not currently sent by Unity.

## Agent to Unity (`agent_out`)

### `agent_response_for_user`
The reply to a question, or the second reply that carries the rest of a request
held for a load.

| field | meaning |
|---|---|
| `response_text` | the full spoken reply |
| `chunks` | the reply split into sentences, played one at a time; a follow-up question is always one chunk |
| `followup_stage` | `true` when the reply asks the user something back (Unity opens the mic after it) |
| `message_id` | unique per publish |
| `nodes` | *optional*: points to highlight, `{"node_1": {id, x, y, <series field>, chunk}}`; `chunk` is the sentence index, missing means the whole reply |
| `rtd_command` | *optional*: `"<dataName>-<chartType>"` loads a chart; `{"filter": {"hidden_series": [...]}}` sets the complete filter |
| `referents` | *optional*: which touch and navigation points the answer used |
| `presentation` | *optional*: `"start"` starts the presentation after this reply is spoken; `"skip"` means this reply's load must not start it (the rest of the request follows) |

Unity applies `rtd_command` before speaking, because every redraw clears highlights.
A filter during the presentation ends it.

### `chart_overview_for_rtd`
Presentation text the agent wrote for a chart with no authored `overview`, sent
after `rtd_data_for_agent`. Not a reply: nothing is spoken.

| field | meaning |
|---|---|
| `data_name` | the chart's data name |
| `chart_type` | the chart type (text is kept per data name and chart type) |
| `overview` | `{title, x_axis, y_axis, <one key per series> or data, summary}`, or `null` when the chart has nothing to describe (no walkthrough) |

## Requests that load a chart

"Open airfares and hide Sydney": the agent sends only the load (with
`presentation: "skip"`), holds "hide Sydney", and runs it once the new chart's
`layer_data_update` arrives, as a separate `agent_response_for_user`. The held part
survives a "which chart?" follow-up; otherwise it is dropped after 20 seconds or when
the user asks something else, and the reply says what didn't run.
