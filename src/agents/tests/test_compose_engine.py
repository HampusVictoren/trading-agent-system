"""How a plain `docker compose up` starts the engine, read from docker-compose.yml.

Stage 7 lifted D2: the engine is no longer behind a profile, and that is safe only because its
default mode places nothing. Both halves are held here, in the Python suite every pull request
runs; the compose job checks the same thing against `docker compose config`.
"""

from pathlib import Path

import yaml

COMPOSE_FILE = Path(__file__).resolve().parents[3] / "docker-compose.yml"


def _services() -> dict:
    return yaml.safe_load(COMPOSE_FILE.read_text(encoding="utf-8"))["services"]


def test_no_service_is_behind_a_profile():
    behind = {name: s["profiles"] for name, s in _services().items() if s.get("profiles")}

    assert behind == {}, "a plain `up` would not start these"


def test_the_engine_defaults_to_shadow_and_only_trading_mode_changes_it():
    assert _services()["engine"]["environment"]["Trading__Mode"] == "${TRADING_MODE:-Shadow}"
