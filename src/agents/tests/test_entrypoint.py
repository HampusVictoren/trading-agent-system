"""`python -m app` is what the container runs, and the order inside it is the point.

The defect this closes was only ever visible in a deployment: uvicorn configures logging
before the application starts, `configure_logging` runs in the lifespan, so the first two
lines of every run - the ones that say whether startup happened - were not JSON while
every line after them was. These tests pin the order, and the fake uvicorn logs from
inside `run()` for that reason: a line emitted after `main()` returned would prove nothing
about which of the two ran first.
"""

import json
import logging
from types import SimpleNamespace

import pytest

from app import __main__ as entrypoint
from app.observability.logging import UVICORN_LOGGERS

# Literal under test; ruff S104 would otherwise flag the intentional broad bind.
BROAD_HOST = "0.0.0.0"  # noqa: S104

# What the fake logs in uvicorn's place. uvicorn's own first line is this one.
FIRST_LINE = "Started server process [%d]"


@pytest.fixture
def restored_logging():
    """configure_logging replaces the root handler, and that would outlive the test."""
    root = logging.getLogger()
    saved_root = (root.handlers[:], root.level)
    saved = {
        name: (logger.handlers[:], logger.propagate, logger.level)
        for name, logger in ((n, logging.getLogger(n)) for n in UVICORN_LOGGERS)
    }

    yield

    root.handlers, root.level = saved_root
    for name, (handlers, propagate, level) in saved.items():
        logger = logging.getLogger(name)
        logger.handlers, logger.propagate, logger.level = handlers, propagate, level


@pytest.fixture
def ran(monkeypatch, restored_logging):
    """Runs main() with uvicorn faked, and answers with what uvicorn was asked for."""
    asked: dict[str, object] = {}

    def fake_run(target: str, **kwargs: object) -> None:
        asked["target"] = target
        asked.update(kwargs)
        # Where uvicorn prints its own first lines: inside run(), before the lifespan.
        logging.getLogger("uvicorn.error").info(FIRST_LINE, 7)

    monkeypatch.setattr(entrypoint.uvicorn, "run", fake_run)

    def run(**declared: object) -> dict[str, object]:
        settings = {"bind_host": "127.0.0.1", "port": 8000} | declared
        monkeypatch.setattr(entrypoint, "get_settings", lambda: SimpleNamespace(**settings))
        entrypoint.main()
        return asked

    return run


def test_uvicorn_is_told_not_to_configure_logging(ran):
    # Anything else and uvicorn's dictConfig would replace the handler installed one line
    # earlier, which is the whole mechanism this entrypoint exists for.
    assert ran()["log_config"] is None


def test_the_declared_bind_is_the_one_uvicorn_gets(ran):
    # TAS_BIND_HOST is read by the startup warning as well, so a second place holding the
    # address is how the warning comes to describe a socket nobody opened.
    asked = ran(bind_host=BROAD_HOST, port=9001)

    assert (asked["host"], asked["port"]) == (BROAD_HOST, 9001)


def test_the_app_is_passed_as_an_import_string(ran):
    # Not the application object: this module would then have to import app.main, and
    # uvicorn imports it again for itself.
    assert ran()["target"] == "app.main:app"


def test_uvicorns_first_lines_are_json(ran, capsys):
    ran()

    written = capsys.readouterr().out.strip().splitlines()
    # Named rather than indexed into: an empty stream means the line went to logging's
    # last-resort handler, which is exactly what happens when configure_logging ran second.
    assert written, (
        "uvicorn's first line reached no JSON handler, so logging was configured too late"
    )
    line = written[-1]

    assert json.loads(line)["message"] == "Started server process [7]"
    assert json.loads(line)["logger"] == "uvicorn.error"
