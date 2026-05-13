"""
Configuration management.
All settings loaded from .env file - the single source of truth.
"""
import os
from pathlib import Path


def _load_dotenv():
    """Load .env file. Required for the agent to run."""
    env_path = Path(__file__).parent.parent / ".env"
    if not env_path.exists():
        raise FileNotFoundError(
            f"Missing .env file at {env_path}\n"
            "Copy .env.example to .env and fill in your values."
        )
    with open(env_path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                os.environ[key.strip()] = value.strip()


_load_dotenv()


def _require(key: str) -> str:
    """Get required env var or raise clear error."""
    value = os.environ.get(key)
    if not value:
        raise ValueError(f"Missing required config: {key} (check your .env file)")
    return value


# =============================================================================
# OpenAI
# =============================================================================
OPENAI_API_KEY = _require("OPENAI_API_KEY")
OPENAI_MODEL = os.environ.get("OPENAI_MODEL", "gpt-4o-mini")
OPENAI_MODEL_CLASSIFIER = os.environ.get("OPENAI_MODEL_CLASSIFIER", OPENAI_MODEL)
OPENAI_MODEL_ANALYSIS = os.environ.get("OPENAI_MODEL_ANALYSIS", OPENAI_MODEL)
OPENAI_MODEL_IMAGE = os.environ.get("OPENAI_MODEL_IMAGE", OPENAI_MODEL)

# =============================================================================
# MQTT
# =============================================================================
MQTT_HOST = _require("MQTT_REMOTE_HOST")
MQTT_PORT = int(os.environ.get("MQTT_REMOTE_PORT", "8883"))
MQTT_USERNAME = _require("MQTT_REMOTE_USERNAME")
MQTT_PASSWORD = _require("MQTT_REMOTE_PASSWORD")
MQTT_TOPIC_IN = os.environ.get("MQTT_TOPIC_IN", "agent_in")
MQTT_TOPIC_OUT = os.environ.get("MQTT_TOPIC_OUT", "agent_out")
