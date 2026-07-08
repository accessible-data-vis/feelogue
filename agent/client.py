"""
Shared OpenAI client instance.
Import this instead of creating new OpenAI() instances.
"""
import os
from openai import OpenAI
from .config import OPENAI_API_KEY

_base_url = os.environ.get("OPENAI_BASE_URL") or None

# Single shared client instance
client = OpenAI(api_key=OPENAI_API_KEY, base_url=_base_url)
