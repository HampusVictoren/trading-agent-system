"""Costly endpoints refuse a flood even from a holder of a valid key."""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient

from app.api.rate_limit import registry
from app.api.security import HEADER as API_KEY_HEADER
from app.dependencies import get_resources
from app.domain.signals import EquityInstrument, RunInfo, Stance, TradeSignal, TradeView
from app.main import app
from app.settings import get_settings
from tests.api_support import API_KEY, api_settings

A_SIGNAL = TradeSignal.from_view(
    TradeView(
        stance=Stance.HOLD,
        conviction=0.31,
        thesis="Ingen tydlig katalysator.",
        key_risks=["Rapporten kan flytta kursen."],
        horizon_days=20,
    ),
    instrument=EquityInstrument(type="equity", symbol="AAPL"),
    reference_price=233.12,
    quote_as_of="2026-09-23T14:03:00Z",  # type: ignore[arg-type]
    run=RunInfo(team_id="default", team_version="8f3a1c9e", revisions=0),
)

A_REQUEST = {
    "instrument": {"type": "equity", "symbol": "AAPL"},
    "team_id": "default",
    "as_of": "2026-09-23T14:02:55Z",
    "existing_position": None,
    "available_risk_budget": 500.0,
    "max_position_pct": 0.05,
    "correlation_id": "0f2d7c11-3b48-4e9a-8c15-77ab2e4d6f30",
}


@pytest.fixture(autouse=True)
def _fresh_buckets():
    registry.reset()
    yield
    registry.reset()
    app.dependency_overrides.clear()


def _client(**settings_overrides):
    async def run(request):
        return A_SIGNAL

    app.dependency_overrides[get_settings] = lambda: api_settings(**settings_overrides)
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
        pipeline=SimpleNamespace(run=run),
        screening=SimpleNamespace(screen=AsyncMock()),
        models=None,
        memory=SimpleNamespace(ping=AsyncMock()),
        http_client=SimpleNamespace(get=AsyncMock()),
        llm_base_url=None,
    )
    return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})


def test_signals_return_429_after_the_per_key_budget():
    client = _client(
        rate_limit_signals_per_minute=2,
        rate_limit_signals_global_per_minute=100,
    )

    assert client.post("/v1/signals", json=A_REQUEST).status_code == 200
    assert client.post("/v1/signals", json=A_REQUEST).status_code == 200
    limited = client.post("/v1/signals", json=A_REQUEST)

    assert limited.status_code == 429
    assert limited.json()["error_code"] == "rate_limited"


def test_signals_return_429_after_the_global_budget():
    # Two different keys share the global ceiling.
    client = _client(
        rate_limit_signals_per_minute=100,
        rate_limit_signals_global_per_minute=2,
    )

    assert client.post("/v1/signals", json=A_REQUEST).status_code == 200
    other = client.post("/v1/signals", json=A_REQUEST, headers={API_KEY_HEADER: API_KEY})
    # Second call with same key still counts against global.
    assert other.status_code == 200
    limited = client.post("/v1/signals", json=A_REQUEST)

    assert limited.status_code == 429
    assert limited.json()["error_code"] == "rate_limited"
