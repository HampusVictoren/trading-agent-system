"""Non-loopback binds warn outside development; Docker setups stay startable."""

import logging
from types import SimpleNamespace

from app.main import _warn_if_bound_broadly

# Literal under test; ruff S104 would otherwise flag the intentional broad bind.
BROAD_HOST = "0.0.0.0"  # noqa: S104


def test_loopback_is_silent(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(SimpleNamespace(bind_host="127.0.0.1", environment="production"))
    assert caplog.records == []


def test_broad_bind_warns_outside_development(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(SimpleNamespace(bind_host=BROAD_HOST, environment="production"))
    assert any(f"TAS_BIND_HOST={BROAD_HOST}" in r.message for r in caplog.records)


def test_broad_bind_is_silent_in_development(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(SimpleNamespace(bind_host=BROAD_HOST, environment="development"))
    assert caplog.records == []
