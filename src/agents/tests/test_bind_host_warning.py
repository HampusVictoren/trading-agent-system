"""Non-loopback binds warn outside development; Docker setups stay startable."""

import logging
from types import SimpleNamespace

from app.main import _warn_if_bound_broadly


def test_loopback_is_silent(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(
            SimpleNamespace(bind_host="127.0.0.1", environment="production")
        )
    assert caplog.records == []


def test_broad_bind_warns_outside_development(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(
            SimpleNamespace(bind_host="0.0.0.0", environment="production")
        )
    assert any("TAS_BIND_HOST=0.0.0.0" in r.message for r in caplog.records)


def test_broad_bind_is_silent_in_development(caplog):
    with caplog.at_level(logging.WARNING):
        _warn_if_bound_broadly(
            SimpleNamespace(bind_host="0.0.0.0", environment="development")
        )
    assert caplog.records == []
