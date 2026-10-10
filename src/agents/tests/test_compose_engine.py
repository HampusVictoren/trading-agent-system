"""How a plain `docker compose up` starts the engine, read from docker-compose.yml.

Stage 7 lifted D2: the engine is no longer behind a profile, and that is safe only because its
default mode places nothing. Both halves are held here, in the Python suite every pull request
runs; the compose job checks the same thing against `docker compose config`.
"""

import re
from pathlib import Path

import pytest
import yaml

COMPOSE_FILE = Path(__file__).resolve().parents[3] / "docker-compose.yml"


def _services() -> dict:
    return yaml.safe_load(COMPOSE_FILE.read_text(encoding="utf-8"))["services"]


def test_no_service_is_behind_a_profile():
    behind = {name: s["profiles"] for name, s in _services().items() if s.get("profiles")}

    assert behind == {}, "a plain `up` would not start these"


# Compose's "${NAME:-default}": the default applies when NAME is unset *or empty*. YAML has already
# removed any quoting, and surrounding whitespace is stripped, so spelling the value differently
# in the file does not matter; what it means does. "${NAME-default}" is refused on purpose - an
# empty TRADING_MODE= would then reach the engine as no mode at all - and so is anything else: a
# literal, a required variable, no default.
_INTERPOLATION = re.compile(r"^\$\{([A-Za-z_][A-Za-z0-9_]*):-([^}]*)\}$")


def _variable_and_default(value: str) -> tuple[str, str]:
    match = _INTERPOLATION.match(value.strip())
    if match is None:
        pytest.fail(f"Trading__Mode is {value!r}, not a ${{VARIABLE:-default}} interpolation")
    return match.group(1), match.group(2)


def test_the_engine_defaults_to_shadow_and_only_trading_mode_changes_it():
    value = str(_services()["engine"]["environment"]["Trading__Mode"])

    assert _variable_and_default(value) == ("TRADING_MODE", "Shadow")


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        ("${TRADING_MODE:-Shadow}", ("TRADING_MODE", "Shadow")),
        ("  ${TRADING_MODE:-Shadow}\n", ("TRADING_MODE", "Shadow")),
        ("${TRADING_MODE:-Paper}", ("TRADING_MODE", "Paper")),
        ("${MODE:-Shadow}", ("MODE", "Shadow")),
    ],
)
def test_the_mode_is_read_by_meaning_not_spelling(value, expected):
    assert _variable_and_default(value) == expected


@pytest.mark.parametrize(
    "value", ["Shadow", "${TRADING_MODE}", "${TRADING_MODE:?set it}", "${TRADING_MODE-Shadow}"]
)
def test_a_mode_without_a_default_is_refused(value):
    with pytest.raises(pytest.fail.Exception):
        _variable_and_default(value)
