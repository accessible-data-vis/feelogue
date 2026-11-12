# Graphy User Manual

Graphy lets you explore charts by touch on a Dot Pad, a refreshable tactile display (RTD). The chart is raised as a grid of pins. Graphy speaks its answers aloud, shows them on the braille row of the Dot Pad, and raises boxes around the data points it mentions.

Graphy works with line charts, scatterplots, bar charts and stacked bar charts, including charts with several series.

There are two ways to interact with Graphy:

- The six physical keys on the Dot Pad, for quick actions such as reading on, stepping through data points, stopping and repeating.
- Talking to Graphy. Talking unlocks every feature, including loading charts, hiding series and asking questions about the data.

This manual is organised in this order:

1. Starting a session: what the facilitator sets up, and your first steps.
2. The Dot Pad layout: the pin area, the keys and the braille row.
3. Knowing what Graphy is doing: tones and the blinking pin.
4. The Dot Pad keys.
5. Talking to Graphy.
6. Loading a chart.
7. The layered presentation.
8. Asking about the data.
9. Hiding and showing series.
10. Messages you may hear.
11. Quick reference.

## 1. Starting a session

A session always follows the same order:

1. The facilitator starts the Graphy assistant on the computer.
2. The facilitator opens the Unity application and presses Play.
3. You load a chart by talking to Graphy, or the facilitator loads one for you.
4. You go through the layered presentation, then explore the chart with the keys and by asking questions.

Steps 1 and 2 are done on the computer by a facilitator. If you are the Dot Pad user, you can skip to section 1.2.

### 1.1 For the facilitator: starting the system

Before you begin:

- Connect the Dot Pad to the computer by USB.
- If finger tracking is used, connect the Leap Motion controller and make sure Ultraleap Hyperion is running.
- Make sure the local MQTT broker, for example Mosquitto, is running on this computer on port 1883. Graphy and Unity talk to each other through it. Without it, nothing works, including the Dot Pad keys.
- Make sure liblouis is installed with Homebrew (`brew install liblouis`). Unity uses it to turn text into braille for the braille row.
- For the wake word, the openWakeWord models must have been downloaded once (see the README). Unity starts the wake word listener itself when you press Play.

Start the Graphy assistant first:

1. Open a terminal in the project folder.
2. Run `venv/bin/python -m agent.run`.
3. Wait for the lines "Connected to MQTT broker" and "Subscribed to 'agent_in'".

Then start Unity:

1. Open the `interaction-manager` project in Unity.
2. Open the scene `Assets/Scenes/Feelogue.unity`.
3. Press Play.
4. If the Dot Pad does not respond, select the RTDUpdater object and tick "Rtd Device Connected" in the Inspector.
5. Check the assistant terminal for the line "Chart metadata index registered". This means Graphy knows which charts exist and is ready to load one by voice.

The order matters. If Unity is started before the assistant, or the assistant is restarted during a session, Graphy cannot find any charts and says "I could not find any chart loaded. Please check your setup". To recover, stop and restart Play in Unity, then load the chart again.

Graphy reads this manual when the assistant starts, to answer help questions. After editing this manual, restart the assistant.

To shut down, press Control and C in the assistant terminal, then stop Play in Unity.

### 1.2 Your first steps

When the system starts, the Dot Pad stays blank and nothing is spoken. This is normal.

To find your way around the Dot Pad first, see section 2.

1. Load a chart. Hold function key 1 until you hear the start tone, then say, for example, "Load the interest rates chart." See section 6.
2. Wait for Graphy to say "Loading the" followed by the chart name. The chart is raised on the pins and its name appears on the braille row.
3. The layered presentation then starts by itself. It walks you through the chart one layer at a time: the title, each axis, each series and a summary. Tap the right arrow key to read on. See section 7.
4. Explore. Feel the chart, step through data points with the arrow keys, and ask questions.

You can also ask Graphy how to do things, for example "How do I hear the rest of the answer?" or "What does the blinking pin mean?" Graphy answers from this manual.

## 2. The Dot Pad layout

From top to bottom, the Dot Pad has a pin area of 60 by 40 pins where the chart is raised, a row of six keys, and a braille row of 20 cells.

### 2.1 The pin area

- The vertical axis runs down the left side, with tick marks pointing left. The horizontal axis runs along the bottom, with tick marks pointing down. The chart sits between them.
- The whole chart is always on the pins. Graphy cannot enlarge part of it or move it.
- On a line chart, each series is drawn as a line through its data points, and each series marks its points with its own symbol, such as a plus, a cross or an arrow pointing up. The line stops a short gap before each symbol, so the symbols stand out.
- On a scatterplot, each point is raised on its own, with no lines. With several groups, each group has its own symbol.
- On a bar chart, each bar is raised with a gap between bars. On a stacked bar chart, each series is one segment of the bar, with its own texture: solid, vertical stripes, a checkerboard, or hollow. The segments are stacked from the bottom up, with a small gap between them.
- Where points of different series fall on the same spot, a solid square block is raised.
- The pin in the bottom right corner blinks while Graphy is working. See section 3.

To find out which symbol or texture a series has, ask Graphy, for example "Which symbol is Memory?"

### 2.2 The keys

The six keys are in one row below the pin area: the left arrow key, function keys 1 to 4, and the right arrow key. The arrow keys are shaped like triangles.

- Arrow keys: tap to read on or read back. Hold for half a second, then release, to step through data points.
- Function key 1: push to talk. Function key 2: stop. Function key 3: repeat. Function key 4: refresh.

Section 4 describes every key action.

### 2.3 The braille row

The braille row is along the bottom edge, below the keys. It shows the text Graphy speaks: each part of an answer, each layer of the layered presentation, the chart name, and the label of a data point. Text longer than 20 cells is split into lines, broken between words. Tap the arrow keys to move between the lines.

## 3. Knowing what Graphy is doing

- A start tone plays when Graphy begins listening.
- An end tone plays when you release the push to talk key.
- While Graphy is working on your request, the pin in the bottom right corner of the pin area blinks about once a second. It stops when the answer arrives.
- If Graphy did not hear anything, the pin stops and Graphy says "I didn't quite get that, please try again." Hold function key 1 and ask again.
- An answer that begins with "First" has more parts waiting. Graphy says "End" after the last part. See section 5.3.

## 4. The Dot Pad keys

Section 2.2 shows where the keys are.

### 4.1 Function keys

- Function key 1: push to talk. Hold it for half a second, until the start tone, and speak. See section 5.1.
- Function key 2: stop. Stops any sound that is playing, cancels listening, stops the blinking pin, and lowers the boxes Graphy raised. It does not cancel a question Graphy is already working on: that answer still arrives.
- Function key 3: repeat. Replays the last sound Graphy played. This is the last short clip, which may be a single part of an answer, a data point label, or just "End". To hear an earlier part again, read back to it. See section 5.3.
- Function key 4: refresh. Redraws the chart, lowers every box on the display, including your own, and shows the chart name on the braille row. It also clears your current data point. Nothing is spoken.

### 4.2 Arrow keys

- Tap the right arrow key: read on. Graphy shows the next line of braille. At the end of a part of an answer, it moves on to the next part and speaks it.
- Tap the left arrow key: read back. Graphy shows the previous line of braille. At the start of a part, it goes back to the previous part and speaks it.
- Hold the left or right arrow key for half a second, then release: move to the previous or next data point. See section 4.4.
- Hold an arrow key and press function key 1, or press function key 1 and then an arrow key: skip a whole part of an answer, or a whole layer of the layered presentation. The right arrow skips forward, the left arrow skips back. This works whatever line of braille is showing.
- Pressing both arrow keys together does nothing.

At the first or last part, tapping further does nothing.

### 4.3 Reading on after stepping or double tapping

Tapping an arrow key moves between the parts of an answer only while the braille row shows that answer. After you step to a data point or double tap one, the braille row shows its label, and a tap only moves through the lines of that label. To get back to the parts of the answer, hold an arrow key and press function key 1.

### 4.4 Stepping through data points

Hold an arrow key for half a second, then release it. The step happens when you release the key.

- Graphy visits only the points raised on the pins. Points of hidden series are skipped.
- On line and bar charts, Graphy moves from left to right. Where several points share a place on the horizontal axis, it visits each series in turn; on a stacked bar, from the bottom segment up.
- On a scatterplot, Graphy moves from left to right, and where points are above each other, the higher one comes first.
- If you have no current point, either arrow key starts at the first point.
- At the first or last point, Graphy stays on it and repeats it.
- The point is marked with a box of pins that blinks briefly and then stays raised. Its label is spoken and shown in braille, for example "2019, 1.17%" or "February 2025, Memory, $190".
- The point you step to becomes "this point" when you talk to Graphy. See section 5.4.
- Loading a chart, moving to another layer of the layered presentation, hiding a series, and refreshing all clear your current point.

### 4.5 What the keys cannot do

These actions are only available by talking to Graphy:

- Loading a chart.
- Hiding a series, or bringing a hidden series back.
- Asking about the data.
- Asking for help.

## 5. Talking to Graphy

### 5.1 Push to talk

1. Hold function key 1 for about half a second.
2. Wait for the start tone.
3. Speak your question or command.
4. Release the key. The end tone plays.
5. The pin in the bottom right corner blinks while Graphy works on your request.

If you release the key before the start tone, nothing happens.

Graphy stops listening when you release the key. This means:

- Keep holding the key until you have finished speaking. Words said after you release it are not heard.
- You can pause while holding the key. Graphy waits for the release, not for a pause.
- Start speaking soon after the tone. If you say nothing for about 8 seconds, Graphy stops listening and says "I didn't quite get that, please try again."
- A request is cut off after about 20 seconds.
- If you press an arrow key while still holding function key 1, the question is cancelled and the end tone plays.

### 5.2 The wake word

You can also say "Hey Graphy" instead of holding a key. The start tone plays, and Graphy listens until you stop talking. Saying "Hey Graphy" does not stop Graphy if it is speaking; press function key 2 first.

### 5.3 Answers in parts

Graphy splits most answers into parts. Each part is one sentence, and each item of a numbered list starts a new part. This lets you take in an answer one sentence at a time, and feel the points each sentence talks about while you hear it.

How an answer plays:

1. When the answer arrives, Graphy speaks the first part only, then waits.
2. If the answer has more than one part, the first part begins with "First".
3. To hear the next part, tap the right arrow key. If the part is longer than the braille row, the first taps show its remaining lines, and the next tap plays the next part. To skip straight to the next part, hold an arrow key and press function key 1. See section 4.2.
4. After the last part, Graphy says "End".

What you hear and feel:

- An answer of one sentence has no "First" and no "End".
- You hear "First" again each time you go back to the first part, and "End" each time you reach the last part.
- Each part replaces the text on the braille row. The braille row shows only the sentence, not "First" or "End".
- Each part raises boxes around the data points and axis labels its sentence talks about, and lowers those of the other parts. A box blinks while it is raised.
- Some boxes stay raised for every part, when a point belongs to the whole answer rather than to one sentence.

Coming back to an answer:

- The parts of the latest answer stay available until Graphy gives a new answer. You can step through data points, stop the sound with function key 2, or refresh, then come back to the parts with an arrow key and function key 1.
- Function key 3 replays the last sound. After the last part, that is just "End". To hear the last part again, read back one part, then forward again.

When Graphy asks you something, such as which chart you meant, the question is a single part, so you hear it in full.

### 5.4 Asking about a point you are touching

You can select a data point in two ways:

- Step to it with the arrow keys, as described in section 4.4.
- Double tap it on the Dot Pad, if finger tracking is set up: two short taps in the same place. Graphy raises a box around it, and speaks and brailles its value. You can also double tap an axis label. The box stays until your next double tap with the same hand, a refresh, or a redraw of the chart.

After that, words like "this", "here" and "that" refer to the point you selected most recently. "These" refers to all of them, for example:

- "What is the value of this point?"
- "How much higher is this than last year?"
- "What is the difference between these points?" when you have selected two points, for example one with each hand, or one by stepping and one by double tapping.

Your double tapped points count until Graphy answers. To ask about a new point, double tap it again.

If what you ask does not fit a single point, such as "Is this going up?", Graphy answers about the chart instead.

### 5.5 Saying several things at once

You can combine requests in one sentence. Graphy carries them out in the order you say them, and gives one answer. Examples:

- "Hide GPU and tell me the average price of Memory."
- "What is the highest year, and what is the lowest?"

A request to load a chart always runs first. The rest runs once the new chart is loaded, and its answer comes separately.

### 5.6 Asking for help

Ask Graphy how to use it, and it answers from this manual in a few short sentences. Like other answers, a help answer may come in parts. Examples:

- "What can you do?"
- "How do I hear the rest of the answer?"
- "How do I load a chart?"
- "What does function key 3 do?"
- "What does the blinking pin mean?"

## 6. Loading a chart

Loading a chart is the first step of every session. It gives Graphy the data and draws the chart on the Dot Pad. You can load a chart by talking to Graphy, or the facilitator can load one from the computer.

Say the name of the chart with a loading word, for example:

- "Load the interest rates chart."
- "Show the water storage chart."
- "Open the component prices chart."

What Graphy says back:

- If one chart matches, Graphy says "Loading the" followed by the chart name. The chart is raised and its name appears on the braille row. The layered presentation then starts by itself. See section 7.
- If several charts match, Graphy says "I found a few possible charts" followed by their names, and asks which one you want. Hold function key 1 again to answer.
- If nothing matches, Graphy says "I couldn't find a chart matching" followed by the name it heard, and names up to three available charts. If the name it says back is not what you said, Graphy misheard you; ask again.

Charts currently available:

- "Interest Rate - Line Chart": the Australian cash rate, one point per year from 2019 to 2025.
- "Computer Component Prices - Line Chart": average prices of Memory, Storage and GPU, every two months from February 2025 to February 2026, in Australian dollars.
- "AAPL Intraday - May 6, 2026": Apple's share price, hourly from 16:00 to 22:00.
- "Melbourne Water Storage - Bar Chart": how full Melbourne's water storage was, one bar per year from 2018 to 2024.
- "Quarterly Revenue by Product Line - Stacked Bar Chart": revenue from Software, Hardware and Services, stacked, for each quarter from Q1 to Q4.
- "Vehicle Power and CO2 Emissions - Scatterplot": 14 cars, their power against their CO2 emissions, grouped as EV, Hybrid and ICE.

## 7. The layered presentation

The layered presentation walks you through a chart one layer at a time, both by touch and by sound. Each layer raises one piece of the chart on the pins and describes it.

The layered presentation starts by itself shortly after a chart loads, once Graphy has prepared it and finished speaking. If you press a key, double tap, or start asking a question before it begins, it does not start. You can ask for it at any time with phrases such as:

- "Give me an overview."
- "Walk me through the chart."
- "Describe the chart."

Graphy says "Here's the overview." and the layered presentation begins.

The layers come in this order:

1. The title layer: the whole chart, with its name and what it shows.
2. The x-axis layer: the horizontal axis on its own.
3. The y-axis layer: the vertical axis on its own.
4. A series layer for each series: that series on its own, with both axes. A chart with one series has a single data layer that shows the whole chart.
5. The summary layer: the main pattern, with the whole chart.

How to move through it:

- The braille row shows the text of each layer. The first layer begins with "First", and Graphy says "End" after the summary layer.
- Tap the right arrow key to read on. At the end of a layer's text, the next tap moves to the next layer.
- Tap the left arrow key to read back. At the start of a layer, it goes back a layer.
- Hold an arrow key and press function key 1 to skip a whole layer.
- At the title layer, reading back does nothing.

During the layered presentation, every series is shown, including any you had hidden. When you read on past the summary layer, Graphy says "End of overview." and hides those series again, telling you which ones, for example "Brisbane is hidden again."

Hiding or showing a series by voice ends the layered presentation straight away, without the announcement. Loading another chart also ends it.

You can ask questions during the layered presentation. Graphy answers about the whole chart, or, during a series layer, about that series unless you name another.

If Graphy cannot prepare the layered presentation for a chart, it says "There's no walkthrough for this chart."

## 8. Asking about the data

Graphy can answer questions about the numbers behind the chart. It works out each answer from the data, so you can ask for values, statistics, comparisons and trends. Examples:

- "What is the maximum?"
- "What was the interest rate in 2020?"
- "What is the average price of Memory?"
- "Which product had the highest revenue in Q3?"
- "Is GPU going up?"
- "What happened after 2022?"
- "What is the value of this point?" after selecting a point.

Things to know about the answers:

- The first sentence names what Graphy understood you asked, for example "Brisbane's average in 2020 is…". If it is not what you asked, Graphy misheard you; ask again.
- Answers that summarise several points, such as an average, end with a sentence saying which points they cover, for example "That covers Memory from February 2025 to February 2026."
- If a question could mean two or three different things, Graphy answers each meaning instead of asking you.
- A yes or no answer comes with its finding in the same sentence, for example "No, Memory and Storage do not cross."
- Graphy answers about the data, not about why it changed. The data records what happened, not the cause.

Finding things on the chart:

- Graphy raises a box around each data point it names, while the sentence that names it is spoken.
- When an answer is about a whole series, such as its trend, every point of that series is boxed.
- Ask where something is, or ask Graphy to highlight it, and it tells you and boxes it. Examples: "Where is 2023?" "Highlight the maximum." "Highlight Memory."

The axes:

- You can ask about the labels on the axes, for example "What are the x-axis labels?" or "What is the y range?" Graphy reads them from the chart and boxes the labels it names.

Colours and symbols:

- You can name a series by the colour it has on screen, for example "the blue line". Graphy works out which series you mean.
- You can ask which symbol or texture a series has on the pins, for example "Which texture is Software?"

## 9. Hiding and showing series

On a chart with several series, you can hide whole series and bring them back. Only whole series can be hidden, not single points or years. Examples:

- "Hide GPU."
- "Only show Memory." This hides every other series.
- "Show Storage again."
- "Show all the series again."

Graphy redraws the chart and says what is now shown, for example "Showing Memory and Storage, with GPU hidden." or "Showing all series." If the chart already looked that way, the answer ends with "as already on the chart."

Things to know:

- At least one series has to stay on the chart. Graphy says "I can't hide every series; at least one has to stay on the chart."
- On a chart with one series, Graphy says "This chart has a single series, so there is nothing to filter."
- If Graphy does not know the series you named, it says "I couldn't find" followed by the name, and lists the chart's series.
- Questions you ask afterwards are about the series still showing, and the answer says which were left out. If you ask about a hidden series by name, Graphy answers from its data and tells you it is hidden.
- Loading a chart shows all its series again.

## 10. Messages you may hear

### 10.1 About loading and the layered presentation

- "Loading the" followed by the chart name.
- "I found a few possible charts" followed by their names, then "Which would you like?"
- "I couldn't find a chart matching" followed by the name Graphy heard, then a few available charts.
- "I don't have a chart loaded yet. Please load a chart first." when you ask for the layered presentation with no chart loaded.
- "There's no walkthrough for this chart." when Graphy cannot prepare the layered presentation.
- "End of overview." when you read on past the summary layer.

### 10.2 About several requests at once

- "I didn't do the rest of your request" followed by what was left, "since no chart was loaded." when a load in the same request did not happen.
- "I loaded the chart, but couldn't do the rest" followed by what was left.

### 10.3 When something goes wrong

- "I didn't quite get that, please try again." when Graphy heard no speech, or could not turn your speech into text. Hold function key 1 and ask again.
- "Sorry, something went wrong looking into that. Could you ask again?"
- "Sorry, I couldn't answer that help question right now."
- "I'm not sure how to help with that."
- "I encountered an error processing your request." when Graphy has a technical problem. Try again, or ask the facilitator to check the assistant.
- "I could not find any chart loaded. Please check your setup" when Graphy does not know which charts exist. The facilitator needs to restart Unity, as described in section 1.1.

## 11. Quick reference

### 11.1 Layout

- From top to bottom: the pin area, the row of six keys, the braille row.
- Line charts: a line through each series, with a symbol at each point.
- Scatterplots: points only. Bar charts: bars; stacked bars use a texture for each series.
- The pin in the bottom right corner blinks while Graphy is working.

### 11.2 Keys

The row of keys, from left to right: left arrow, function keys 1 to 4, right arrow.

- Talk to Graphy: hold function key 1 until the start tone, speak, then release.
- Stop sound: press function key 2.
- Repeat the last sound: press function key 3.
- Refresh the display: press function key 4.
- Read on, or read back: tap the right or left arrow key.
- Skip a whole part or layer: hold an arrow key and press function key 1.
- Next or previous data point: hold the right or left arrow key for half a second, then release.

### 11.3 Voice

- Load a chart: "Load the interest rates chart."
- Layered presentation: "Give me an overview."
- Help: "How do I hear the rest of the answer?"
- Data question: "What was the average in 2020?"
- Trend question: "Is Memory going up?"
- Find something: "Where is the maximum?" or "Highlight Memory."
- Axis question: "What are the x-axis labels?"
- Hide a series: "Hide GPU."
- Bring a series back: "Show GPU again." or "Show all the series again."
- A point you selected: "What is the value of this point?"
