from pathlib import Path
from dotenv import load_dotenv

# Läs in src/agents/.env innan några undermoduler importeras (memory.py läser miljövariabler vid import).
# Variabler som redan är satta i skalet har företräde.
load_dotenv(Path(__file__).resolve().parent.parent / ".env")
