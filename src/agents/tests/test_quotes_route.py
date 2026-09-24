"""The deterministic endpoint, from the engine's side of the wire.

There is no model here and nothing to interpret: the interesting behaviour is what the path
parameter is allowed to be, and what happens when the provider cannot answer.
"""

import json
from datetime import UTC, datetime
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr, ValidationError

from app.api.security import HEADER as API_KEY_HEADER
from app.application.errors import InstrumentNotFound, MarketDataUnavailable
from app.dependencies import get_resources
from app.domain.facts import MarketSnapshot, PriceBar, Quote
from app.domain.quotes import InstrumentQuote
from app.domain.signals import EquityInstrument
from app.main import app
from app.settings import get_settings

API_KEY = "a-test-key-of-some-length"

CONTRACT = Path(__file__).resolve().parents[3] / "contracts" / "quote.schema.json"
EXAMPLE = Path(__file__).resolve().parents[3] / "contracts" / "examples" / "quote.json"

A_SNAPSHOT = MarketSnapshot(
    quote=Quote(
        symbol="MSFT",
        currency="USD",
        price=415.25,
        pe_ratio=34.1,
        sector="Technology",
        as_of=datetime(2026, 9, 24, 18, 44, tzinfo=UTC),
    ),
    history=(PriceBar(on=datetime(2026, 9, 23).date(), close=410.0),),
)


@pytest.fixture
def client(monkeypatch):
    """The app with its market data substituted, and nothing else."""
    market = AsyncMock()
    market.snapshot.return_value = A_SNAPSHOT

    settings = SimpleNamespace(agent_api_key=SecretStr(API_KEY))
    app.dependency_overrides[get_settings] = lambda: settings
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(market=market)

    with TestClient(app) as test_client:
        test_client.market = market  # type: ignore[attr-defined]
        yield test_client

    app.dependency_overrides.clear()


def get(client: TestClient, symbol: str, key: str | None = API_KEY):
    headers = {} if key is None else {API_KEY_HEADER: key}
    return client.get(f"/v1/quotes/{symbol}", headers=headers)


def test_a_quote_comes_back_in_the_shape_the_contract_describes(client):
    response = get(client, "MSFT")

    assert response.status_code == 200
    assert response.json() == {
        "instrument": {"type": "equity", "symbol": "MSFT"},
        "price": 415.25,
        "currency": "USD",
        "as_of": "2026-09-24T18:44:00Z",
    }


def test_the_fact_sheets_own_fields_do_not_leak_into_the_answer(client):
    # P/E and sector are read by agents, not by arithmetic. Every field in a contract is a
    # field the other side has to keep accepting, so the engine gets only what it uses.
    body = get(client, "MSFT").json()

    assert "pe_ratio" not in body
    assert "sector" not in body


@pytest.mark.parametrize(
    "symbol",
    [
        "../internal/shutdown",
        "..%2Fsecrets",
        "msft",  # the engine normalises before it asks; lower case is somebody else
        "TOOLONGSYMBOL",
        "1MSFT",
    ],
)
def test_a_symbol_that_is_not_a_symbol_is_refused_before_any_lookup(client, symbol):
    # Finding B was not "a symbol in a path" but "a symbol nobody checked". The pattern runs
    # before the route body, so the provider is never asked.
    response = get(client, symbol)

    assert response.status_code in (404, 422)
    client.market.snapshot.assert_not_called()


def test_a_quote_is_not_free_to_ask_for(client):
    # It costs no model call, but it does cost a market data call and it exposes the one
    # integration this system has. The dependency is on the router, so the route is closed
    # by construction rather than by remembering.
    assert get(client, "MSFT", key=None).status_code == 401
    assert get(client, "MSFT", key="wrong").status_code == 401
    client.market.snapshot.assert_not_called()


def test_an_unknown_symbol_is_the_callers_problem(client):
    client.market.snapshot.side_effect = InstrumentNotFound("no data for NOSUCH")

    response = get(client, "NOSUCH")

    assert response.status_code == 422
    assert response.json()["error_code"] == "instrument_not_found"


def test_a_market_data_outage_is_not_the_callers_problem(client):
    client.market.snapshot.side_effect = MarketDataUnavailable("yfinance timed out")

    response = get(client, "MSFT")

    assert response.status_code == 503
    assert response.json()["error_code"] == "market_data_unavailable"


def test_the_answer_never_carries_a_symbol_the_provider_invented(client):
    # The provider echoes a symbol back and nothing validates it. The response is built from
    # the path parameter, which FastAPI has already checked.
    client.market.snapshot.return_value = A_SNAPSHOT.model_copy(
        update={"quote": A_SNAPSHOT.quote.model_copy(update={"symbol": "../elsewhere"})}
    )

    body = get(client, "MSFT").json()

    assert body["instrument"]["symbol"] == "MSFT"


def test_the_checked_in_example_reads_into_the_model():
    quote = InstrumentQuote.model_validate(json.loads(EXAMPLE.read_text(encoding="utf-8")))

    assert isinstance(quote.instrument, EquityInstrument)
    assert quote.price > 0


def test_the_contract_and_the_model_require_the_same_fields():
    schema = json.loads(CONTRACT.read_text(encoding="utf-8"))

    assert set(schema["required"]) == set(InstrumentQuote.model_fields)
    assert schema["unevaluatedProperties"] is False


def test_the_model_refuses_a_field_the_contract_does_not_have():
    # unevaluatedProperties: false on one side has to mean extra="forbid" on the other, or
    # the schema is a description rather than a rule.
    example = json.loads(EXAMPLE.read_text(encoding="utf-8"))

    with pytest.raises(ValidationError):
        InstrumentQuote.model_validate(example | {"pe_ratio": 34.1})
