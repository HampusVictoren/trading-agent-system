"""A key that opens one door must not open the others."""

from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr

from app.api.security import HEADER as API_KEY_HEADER
from app.dependencies import get_resources
from app.domain.signals import EquityInstrument, RunInfo, Stance, TradeSignal, TradeView
from app.main import app
from app.settings import get_settings
from tests.api_support import API_KEY, api_settings

SIGNALS_KEY = "signals-only-key-of-length"
MARKET_KEY = "market-only-key-of-length"
OUTCOMES_KEY = "outcomes-only-key-length"

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
def _clear():
    yield
    app.dependency_overrides.clear()


def _client():
    async def run(request):
        return A_SIGNAL

    app.dependency_overrides[get_settings] = lambda: api_settings(
        agent_api_key=SecretStr(API_KEY),
        agent_api_key_signals=SecretStr(SIGNALS_KEY),
        agent_api_key_market=SecretStr(MARKET_KEY),
        agent_api_key_outcomes=SecretStr(OUTCOMES_KEY),
    )
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
        pipeline=SimpleNamespace(run=run),
        outcomes=AsyncMock(),
        market=SimpleNamespace(snapshot=AsyncMock()),
        screening=SimpleNamespace(screen=AsyncMock()),
        models=None,
        memory=SimpleNamespace(ping=AsyncMock()),
        http_client=SimpleNamespace(get=AsyncMock()),
        llm_base_url=None,
    )
    return TestClient(app, raise_server_exceptions=False)


def test_a_signals_key_may_start_an_analysis():
    response = _client().post(
        "/v1/signals", json=A_REQUEST, headers={API_KEY_HEADER: SIGNALS_KEY}
    )
    assert response.status_code == 200


def test_a_market_key_cannot_start_an_analysis():
    response = _client().post(
        "/v1/signals", json=A_REQUEST, headers={API_KEY_HEADER: MARKET_KEY}
    )
    assert response.status_code == 401
    assert response.json()["error_code"] == "unauthorized"


def test_a_signals_key_cannot_post_outcomes():
    body = {
        "outcomes": [
            {
                "correlation_id": "c-1",
                "horizon_unit": "TradingDays",
                "horizon_days": 5,
                "status": "NotMeasurable",
                "reason": "no bars",
                "benchmark_symbol": "^GSPC",
            }
        ]
    }
    response = _client().post(
        "/v1/outcomes", json=body, headers={API_KEY_HEADER: SIGNALS_KEY}
    )
    assert response.status_code == 401


def test_the_legacy_key_still_opens_every_door():
    response = _client().post(
        "/v1/signals", json=A_REQUEST, headers={API_KEY_HEADER: API_KEY}
    )
    assert response.status_code == 200
