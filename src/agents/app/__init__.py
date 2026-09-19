from pathlib import Path

from dotenv import load_dotenv

# Load src/agents/.env before any submodule is imported
# (memory.py reads environment variables at import time).
# Variables already set in the shell take precedence.
load_dotenv(Path(__file__).resolve().parent.parent / ".env")
