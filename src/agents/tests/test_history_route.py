"""The history endpoint: the engine's trading calendar as much as its price series.

What matters here is the window, the fact that an empty answer is a good answer, and that
nothing the provider echoes back reaches the response unchecked.
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
from app.domain.quotes import InstrumentHistory
from app.domain.signals import MAX_SYMBOL_LENGTH, EquityInstrument
from app.main import app
from app.settings import get_settings

API_KEY = "a-test-key-of-some-length"

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"

# 21 September 2026 is a Monday; the 24th is missing as a holiday and the 26th and 27th are
# a weekend. The gaps are the point: they are what a trading calendar is made of.
BARS = (
    PriceBar(on=datetime(2026, 9, 21).date(), close=410.10),
    PriceBar(on=datetime(2026, 9, 22).date(), close=412.75),
    PriceBar(on=datetime(2026, 9, 23).date(), close=408.40),
    PriceBar(on=datetime(2026, 9, 25).date(), close=415.25),
)

A_SNAPSHOT = MarketSnapshot(
    quote=Quote(
        symbol="MSFT",
        currency="USD",
        price=415.25,
        pe_ratio=34.1,
        sector="Technology",
        as_of=datetime(2026, 9, 25, 20, 0, tzinfo=UTC),
    ),
    history=BARS,
)


@pytest.fixture
def history():
    """The route with its market data substituted, and no lifespan: entering the client
    would open a database pool and probe the LLM backend, neither of which a unit test has."""
    market = AsyncMock()
    market.snapshot.return_value = A_SNAPSHOT

    app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
        agent_api_key=SecretStr(API_KEY)
    )
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(market=market)

    yield TestClient(app, raise_server_exceptions=False), market

    app.dependency_overrides.clear()


def get(
    client: TestClient, symbol: str, since: str | None = "2026-09-21", key: str | None = API_KEY
):
    headers = {} if key is None else {API_KEY_HEADER: key}
    params = {} if since is None else {"from": since}
    return client.get(f"/v1/quotes/{symbol}/history", headers=headers, params=params)


def test_the_whole_window_comes_back_oldest_first(history):
    client, _ = history

    body = get(client, "MSFT").json()

    assert body["instrument"] == {"type": "equity", "symbol": "MSFT"}
    assert body["bars"] == [
        {"on": "2026-09-21", "close": 410.10},
        {"on": "2026-09-22", "close": 412.75},
        {"on": "2026-09-23", "close": 408.40},
        {"on": "2026-09-25", "close": 415.25},
    ]


def test_the_window_starts_where_the_caller_says(history):
    # The caller always knows the date it cares about: the day the signal was made.
    client, _ = history

    body = get(client, "MSFT", since="2026-09-23").json()

    assert [bar["on"] for bar in body["bars"]] == ["2026-09-23", "2026-09-25"]


def test_a_day_the_market_was_shut_is_simply_absent(history):
    # No holiday table says the 24th was closed, and none says the 26th was a Saturday. The
    # missing bars are what the engine counts by.
    client, _ = history

    days = [bar["on"] for bar in get(client, "MSFT").json()["bars"]]

    assert "2026-09-24" not in days
    assert "2026-09-26" not in days


def test_nothing_since_that_date_is_an_answer_rather_than_an_error(history):
    # The engine reads an empty window as a horizon that has not passed. A 404 would read as
    # "no such instrument", which is a different thing entirely.
    client, _ = history

    response = get(client, "MSFT", since="2026-10-01")

    assert response.status_code == 200
    assert response.json()["bars"] == []


def test_the_window_is_required(history):
    # An unbounded history would be a different, larger endpoint every time the provider's
    # window changed.
    client, market = history

    assert get(client, "MSFT", since=None).status_code == 422
    market.snapshot.assert_not_called()


def test_a_date_that_is_not_a_date_is_refused_before_any_lookup(history):
    client, market = history

    assert get(client, "MSFT", since="yesterday").status_code == 422
    market.snapshot.assert_not_called()


# The last case is derived rather than spelled out: it was the literal "TOOLONGSYMBOL" until
# the cap moved from ten to sixteen for Swedish tickers, at which point it silently became a
# valid symbol and stopped testing anything.
@pytest.mark.parametrize("symbol", ["../internal/shutdown", "msft", "A" * (MAX_SYMBOL_LENGTH + 1)])
def test_a_symbol_that_is_not_a_symbol_is_refused_before_any_lookup(history, symbol):
    client, market = history

    assert get(client, symbol).status_code in (404, 422)
    market.snapshot.assert_not_called()


def test_history_is_not_free_to_ask_for(history):
    client, market = history

    assert get(client, "MSFT", key=None).status_code == 401
    assert get(client, "MSFT", key="wrong").status_code == 401
    market.snapshot.assert_not_called()


def test_an_unknown_symbol_and_an_outage_keep_their_own_codes(history):
    client, market = history

    market.snapshot.side_effect = InstrumentNotFound("no data for NOSUCH")
    assert get(client, "NOSUCH").json()["error_code"] == "instrument_not_found"

    market.snapshot.side_effect = MarketDataUnavailable("yfinance timed out")
    assert get(client, "MSFT").json()["error_code"] == "market_data_unavailable"


def test_the_answer_never_carries_a_symbol_the_provider_invented(history):
    client, market = history
    market.snapshot.return_value = A_SNAPSHOT.model_copy(
        update={"quote": A_SNAPSHOT.quote.model_copy(update={"symbol": "../elsewhere"})}
    )

    assert get(client, "MSFT").json()["instrument"]["symbol"] == "MSFT"


def test_the_checked_in_example_reads_into_the_model():
    example = json.loads(
        (CONTRACTS / "examples" / "quote-history.json").read_text(encoding="utf-8")
    )

    parsed = InstrumentHistory.model_validate(example)

    assert isinstance(parsed.instrument, EquityInstrument)
    assert len(parsed.bars) == 4


def test_the_contract_and_the_model_require_the_same_fields():
    schema = json.loads((CONTRACTS / "quote-history.schema.json").read_text(encoding="utf-8"))

    assert set(schema["required"]) == set(InstrumentHistory.model_fields)
    assert schema["unevaluatedProperties"] is False


def test_the_model_refuses_a_field_the_contract_does_not_have():
    example = json.loads(
        (CONTRACTS / "examples" / "quote-history.json").read_text(encoding="utf-8")
    )

    with pytest.raises(ValidationError):
        InstrumentHistory.model_validate(example | {"currency": "USD"})
