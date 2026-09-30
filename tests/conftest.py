import os
import sys
from pathlib import Path

# Model calls are replaced in the tests; the key only lets the agent import.
os.environ.setdefault("OPENAI_API_KEY", "test-key-not-used")

REPO = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(REPO))
sys.path.insert(0, str(REPO / "agent"))
