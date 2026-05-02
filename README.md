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
- Google Cloud project with Speech-to-Text and Text-to-Speech APIs enabled
- Picovoice Porcupine access key (free tier at console.picovoice.ai)
- MQTT broker (tested with HiveMQ Cloud free tier)
- OpenAI API key

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
```

**3. Add credential files to `interaction-manager` folder**

- `key-service-account-google.json` — Google Cloud service account key
- `key-porcupine.json` — Porcupine access key and wake word path:
  ```json
  {
    "access_key": "your-key-here",
    "keyword_path": "your-wake-word.ppn"
  }
  ```
  Train and download a custom wake word `.ppn` file at [console.picovoice.ai](https://console.picovoice.ai), then place it in `interaction-manager/`.

**4. Add charts**

A sample chart is included: `interaction-manager/Assets/StreamingAssets/compiled-vl-interestrates-line-new.json` (Australian interest rates, 2019–2025). To add your own, place compiled Vega-Lite JSON files named `compiled-vl-{dataName}-{chartType}-new.json` into `interaction-manager/Assets/StreamingAssets/`.

**5. Open Unity project**

Open `interaction-manager/` in Unity 2022.3. On first open, Unity will automatically download the Ultraleap Tracking package via Package Manager (requires internet). Open the scene `Assets/Scenes/Feelogue.unity` if it does not load automatically.

**6. Run the agent**

```bash
source venv/bin/activate
jupyter notebook
```

Open `agent.ipynb` and run all cells.

**7. Run the Interaction Manager**

Ensure the Dot Pad is connected via USB and Ultraleap Hyperion is running, then press **Play** in the Unity Editor. Select a chart from the dropdown in the Game View to load it onto the RTD, or say your trained wake word followed by a load command such as *"load the interest rates chart"*.
