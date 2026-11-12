# Feelogue: Reference Implementation

![Teaser](docs/teaser.png)

Reference implementation for the paper:

> **Supporting Multimodal Data Interaction on Refreshable Tactile Displays: An Architecture to Combine Touch and Conversational AI**
> Samuel Reinders, Munazza Zaib, Matthew Butler, Bongshin Lee, Ingrid Zukerman, Lizhen Qu, Kim Marriott
>
> PacificVis 2026 - https://doi.org/10.48550/arXiv.2602.15280

The system combines touch input with a conversational AI agent on a refreshable tactile display (RTD), enabling deictic queries that fuse touch context with spoken language. For example, touching two data points and asking *"what is the trend between these points?"*

> **Note:** Stand design files (for mounting the Leap Motion Controller above the Dot Pad) will be added shortly.

## Architecture

![Architecture](docs/architecture.png)

Three components communicate via MQTT:

- **Hardware Devices**: Dot Pad RTD (tactile output, USB serial) + Ultraleap Leap Motion Controller 2 (finger tracking)
- **Interaction Manager** (`interaction-manager/`): Unity C# application. Processes touch and speech input, renders Vega-Lite charts as tactile pin-grid representations, and coordinates multimodal output (tactile, Braille, audio)
- **Conversational Agent** (`agent/`): Python. Classifies intent, resolves deictic references using touch context, and runs a LangChain/GPT-4o calculation pipeline for data queries

The messages between Unity and the agent are described in [docs/messages.md](docs/messages.md).
How to add a chart, and how the agent writes its presentation text, is in [docs/charts.md](docs/charts.md).

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

## Setup

**1. Clone and configure environment**

```bash
git clone <repo-url>
cd <repo>
cp .env.example .env
# Fill in .env with your credentials
```

Set `DOT_PAD_PORT` in `.env` to your Dot Pad's serial port. To find it, connect the Dot Pad via USB and run:
```bash
ls /dev/tty.*
```

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

**4. Add charts**

A sample chart is included: `interaction-manager/Assets/StreamingAssets/compiled-vl-interestrates-line-new.json` (Australian interest rates, 2019–2025). To add your own, place compiled Vega-Lite JSON files named `compiled-vl-{dataName}-{chartType}-new.json` into `interaction-manager/Assets/StreamingAssets/`.

**5. Open Unity project**

Open `interaction-manager/` in Unity 2022.3. On first open, Unity will automatically download the Ultraleap Tracking package via Package Manager (requires internet). Open the scene `Assets/Scenes/Feelogue.unity` if it does not load automatically.

**6. Run the agent**

Start a local broker first (`brew install mosquitto`, then `brew services start mosquitto`), then:

```bash
source venv/bin/activate
python -m agent.run            # local broker, localhost:1883 (default; --local says the same)
python -m agent.run --remote   # remote broker from .env (TLS)
```

Unity also starts on the local broker. The **MQTT-L / MQTT-R** button in the Game View switches it to the remote broker and back; the agent and Unity must be on the same one. `agent.ipynb` still works for stepping through the agent by hand.

Run the agent's tests with `python -m pytest tests/`. They need no `.env` or API key: model calls are replaced in the tests.

**7. Run the Interaction Manager**

Ensure the Dot Pad is connected via USB and Ultraleap Hyperion is running, then press **Play** in the Unity Editor. Select a chart from the dropdown in the Game View to load it onto the RTD, or say "Hey Graphy" followed by a load command such as *"load the interest rates chart"*.
