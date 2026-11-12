# Graphy: Conversational Tactile Data Interface

![Teaser](docs/teaser.png)

This repository is the system behind two companion papers, each one half of the same project:

- **Graphy** (IEEE VIS 2026) is the co-design: how the interface was designed with blind and low-vision co-designers on the Feelogue architecture, and the design recommendations that came out of it.
- **Feelogue** (IEEE PacificVis 2026) is the architecture: how touch on a refreshable tactile display (RTD) and a conversational AI agent work together, so that someone can touch the chart and ask about what they are touching.

The code on `main` is Graphy, running on the Feelogue architecture.

**Graphy** (IEEE VIS 2026)

> **Conversational Tactile Data Interfaces: Co-Designing Accessible Data Experiences with Blind Users Using Refreshable Tactile Displays and Conversational AI**
> Samuel Reinders, Munazza Zaib, Bongshin Lee, Ingrid Zukerman, Matthew Butler, Thien Autran, Sascha Cowley, Francois Jacobs, Lizhen Qu, Kim Marriott
>
> IEEE VIS 2026, published in IEEE TVCG - https://doi.org/10.48550/arXiv.2607.14588
>
> **Best Paper Honorable Mention**

Graphy was co-designed with blind and low-vision co-designers. Touch gives the spatial picture of the data, and the agent does the calculation and analysis. The paper's design recommendations include a layered presentation of each chart, a feedback grammar for highlights, and a select-confirm-ask-verify way of asking about points.

**Feelogue** (IEEE PacificVis 2026)

> **Supporting Multimodal Data Interaction on Refreshable Tactile Displays: An Architecture to Combine Touch and Conversational AI**
> Samuel Reinders, Munazza Zaib, Matthew Butler, Bongshin Lee, Ingrid Zukerman, Lizhen Qu, Kim Marriott
>
> IEEE PacificVis 2026 - https://doi.org/10.48550/arXiv.2602.15280
>
> **Best Paper Award (VisNotes Track)**

Feelogue combines touch input with a conversational AI agent on an RTD, enabling deictic queries that fuse touch context with spoken language, for example touching two data points and asking *"what is the trend between these points?"* The code as published with that paper is at the tag [`pacificvis-2026`](https://github.com/accessible-data-vis/feelogue/tree/pacificvis-2026).

> **Note:** Stand design files (for mounting the Leap Motion Controller above the Dot Pad) will be added shortly.

## What Graphy adds

- A layered presentation of each chart: the title, each axis, each series on its own, and a summary, stepped through with the Dot Pad keys
- Line charts, scatterplots, bar charts and stacked bar charts
- Answers spoken one sentence at a time, each highlighting the points it names, following the paper's feedback grammar
- Selecting points by double tap or by stepping through them, then asking about "this" or "these"
- Hiding and showing series by voice

## Documentation

- [User manual](docs/user_manual.md): using Graphy with the Dot Pad keys and by voice. Graphy's help answers come from it.
- [Adding a chart](docs/charts.md): the chart spec, the presentation text, and the preview image.
- [Messages](docs/messages.md): what Unity and the agent send each other.
- [Example charts](interaction-manager/Assets/StreamingAssets/): six charts to try, covering every chart type Graphy supports:
  - Interest Rate - Line Chart: the Australian cash rate, yearly, 2019 to 2025
  - Computer Component Prices - Line Chart: Memory, Storage and GPU prices, every two months, February 2025 to February 2026
  - AAPL Intraday - May 6, 2026: Apple's share price, hourly
  - Melbourne Water Storage - Bar Chart: yearly, 2018 to 2024
  - Quarterly Revenue by Product Line - Stacked Bar Chart: Software, Hardware and Services, Q1 to Q4
  - Vehicle Power and CO2 Emissions - Scatterplot: 14 cars, grouped as EV, Hybrid and ICE

## Architecture

![Architecture](docs/architecture.png)

Three components communicate via MQTT:

- **Hardware Devices**: Dot Pad RTD (tactile output, USB serial) + Ultraleap Leap Motion Controller 2 (finger tracking)
- **Interaction Manager** (`interaction-manager/`): Unity C# application. Processes touch and speech input, renders Vega-Lite charts as tactile pin-grid representations, and coordinates multimodal output (tactile, Braille, audio)
- **Conversational Agent** (`agent/`): Python. Classifies intent, resolves deictic references using touch context, and answers data questions with a LangGraph agent that runs pandas on the chart's data

## Requirements

**Platform**
- macOS (arm64 or x86_64) — Windows support is in progress

**Hardware**
- Dot Pad RTD
- Ultraleap Leap Motion Controller 1 or 2

**Software**
- Unity 2022.3 LTS or later
- Ultraleap Hyperion — must be running before launching the Interaction Manager
- Python 3.10+
- Google Cloud project with the Text-to-Speech API enabled
- MQTT broker: a local [Mosquitto](https://mosquitto.org) on `localhost:1883` (the default), or a remote broker (tested with HiveMQ Cloud free tier)
- OpenAI API key (used by the agent and for speech-to-text)
- liblouis, installed with Homebrew (`brew install liblouis`), for UEB grade 1 or grade 2 braille on the braille row. Without it, Graphy uses its own letter-by-letter braille.

## Setup

**1. Clone and configure environment**

```bash
git clone https://github.com/accessible-data-vis/feelogue.git
cd feelogue
cp .env.example .env
```

Fill in `.env`:
- `OPENAI_API_KEY`: your OpenAI API key.
- `PYTHON_PATH`: the Python of the environment you create in step 2, for example `/path/to/feelogue/venv/bin/python3`. Unity runs the speech-to-text, wake word and text-to-speech scripts with it.
- `SERIAL_PORT_PATH`: your Dot Pad's serial port. To find it, connect the Dot Pad via USB and run `ls /dev/cu.*`.

**2. Create Python environment**

```bash
python3 -m venv venv
venv/bin/pip install -r requirements.txt
venv/bin/python -c "import openwakeword.utils as u; u.download_models()"
```

The last line downloads openWakeWord's shared models once (needs internet).

**3. Add credential files to `interaction-manager` folder**

- `key-service-account-google.json` — Google Cloud service account key

**Wake word.** Saying "Hey Graphy" starts a question without holding F1. The wake word runs on [openWakeWord](https://github.com/dscripka/openWakeWord) in a background Python process that Unity starts at Play. To change it, set `Wake Word Model` on the `AgentWakeWord` component in the Inspector:
- a trained `.onnx` model placed in `interaction-manager/Assets/StreamingAssets/keyword_files/`, entered as `keyword_files/your_model.onnx`
- a pre-trained openWakeWord model name, such as `hey_jarvis` or `alexa` (these models are licensed CC BY-NC-SA 4.0, non-commercial use only)

**Speech-to-text.** Questions are transcribed by OpenAI (`gpt-transcribe` over the Realtime API) with `OPENAI_API_KEY` from `.env`. Set `OPENAI_TRANSCRIBE_MODEL` in `.env` to use another model. To use Google Speech-to-Text instead, set `Stt Script` on the `SpeechToText` component to `google_cloud_speechtotext_v1.py` and enable the Speech-to-Text API in your Google Cloud project.

**4. Charts**

Six example charts are included (see [Documentation](#documentation)). To add your own, see [docs/charts.md](docs/charts.md).

**5. Open Unity project**

Open `interaction-manager/` in Unity 2022.3. On first open, Unity will automatically download the Ultraleap Tracking package via Package Manager (requires internet). Open the scene `Assets/Scenes/Feelogue.unity` if it does not load automatically.

The scene starts with the Dot Pad switched off, so it runs without one. To use a Dot Pad, select **RTDUpdater** in the Hierarchy and tick **Rtd Device Connected** in the Inspector.

The braille row uses UEB grade 2 by default. To change it, select **VegaChartLoader** and set **Braille Mode** in the Inspector.

**6. Run the agent**

Start a local broker first (`brew install mosquitto`, then `brew services start mosquitto`), then:

```bash
source venv/bin/activate
python -m agent.run            # local broker, localhost:1883 (default; --local says the same)
python -m agent.run --remote   # remote broker from .env (TLS)
```

Start the agent before pressing Play: Unity tells the agent which charts exist when it starts, so an agent started afterwards cannot load charts by voice.

Unity also starts on the local broker. The **MQTT-L / MQTT-R** button in the Game View switches it to the remote broker and back; the agent and Unity must be on the same one. `agent.ipynb` still works for stepping through the agent by hand.

**7. Run the Interaction Manager**

Ensure the Dot Pad is connected via USB and Ultraleap Hyperion is running, then press **Play** in the Unity Editor. Select a chart from the dropdown in the Game View to load it onto the RTD, or say "Hey Graphy" followed by a load command such as *"load the interest rates chart"*. The layered presentation then starts by itself. The [user manual](docs/user_manual.md) describes the keys and everything you can say.

## Tests

Run the agent's tests with `python -m pytest tests/`. They need no `.env` or API key: model calls are replaced in the tests.
