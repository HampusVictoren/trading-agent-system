"""POST /v1/outcomes, from the engine's side of the wire.

There is no model here either. What matters is that the checked-in contract and this
service agree on the shape, that a failure to store is reported rather than swallowed, and
that the endpoint is closed like every other one that is not a health check.
"""

import json
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from fastapi.testclient import TestClient
from pydantic import SecretStr, ValidationError

from app.api.security import HEADER as API_KEY_HEADER
from app.dependencies import get_resources
from app.domain.outcomes import MAX_OUTCOMES_PER_REQUEST, HorizonUnit, OutcomeReport, OutcomeStatus
from app.main import app
from app.settings import get_settings

API_KEY = "a-test-key-of-some-length"

CONTRACTS = Path(__file__).resolve().parents[3] / "contracts"
EXAMPLES = CONTRACTS / "examples"
REPORT_EXAMPLES = ["outcomes-measured.json", "outcomes-not-measurable.json"]


def example(name: str) -> dict:
    return json.loads((EXAMPLES / name).read_text(encoding="utf-8"))


@pytest.fixture
def outcomes():
    """The route with its store substituted.

    Constructed rather than entered as a context manager, because entering runs the
    lifespan - a database pool and an LLM probe, neither of which a unit test may need.
    """
    store = AsyncMock()

    app.dependency_overrides[get_settings] = lambda: SimpleNamespace(
        agent_api_key=SecretStr(API_KEY)
    )
    app.dependency_overrides[get_resources] = lambda: SimpleNamespace(outcomes=store)

    yield TestClient(app, raise_server_exceptions=False), store

    app.dependency_overrides.clear()


def post(client: TestClient, body: dict, key: str | None = API_KEY):
    headers = {} if key is None else {API_KEY_HEADER: key}
    return client.post("/v1/outcomes", json=body, headers=headers)


@pytest.mark.parametrize("name", REPORT_EXAMPLES)
def test_the_checked_in_examples_are_accepted(outcomes, name):
    """The engine's suite reads the same files. A field added on one side only becomes a
    failure here rather than a 422 during a live sweep."""
    client, store = outcomes

    response = post(client, example(name))

    assert response.status_code == 200
    assert response.json() == {"accepted": len(example(name)["outcomes"])}
    assert store.store.await_count == 1


def test_a_measured_outcome_keeps_the_numbers_the_engine_sent(outcomes):
    client, store = outcomes

    post(client, example("outcomes-measured.json"))

    stored = store.store.await_args.args[0]
    assert [o.horizon_unit for o in stored] == [
        HorizonUnit.TRADING_DAYS,
        HorizonUnit.CALENDAR_DAYS,
    ]
    assert stored[0].status is OutcomeStatus.MEASURED
    assert stored[0].net_edge == 0.014884
    assert stored[0].hit is True
    assert stored[1].hit is False


def test_a_row_that_could_not_be_measured_is_still_a_row(outcomes):
    """Reported rather than left out: a signal that is quietly never measured is one
    missing from the population any report speaks about."""
    client, store = outcomes

    response = post(client, example("outcomes-not-measurable.json"))

    assert response.status_code == 200
    stored = store.store.await_args.args[0]
    assert stored[0].status is OutcomeStatus.NOT_MEASURABLE
    assert stored[0].reason
    assert stored[0].hit is None
    assert stored[0].instrument_return is None


def test_a_sweep_nobody_authorised_is_refused(outcomes):
    client, store = outcomes

    response = post(client, example("outcomes-measured.json"), key=None)

    assert response.status_code == 401
    assert response.json()["error_code"] == "unauthorized"
    store.store.assert_not_awaited()


def test_a_failure_to_store_is_reported_rather_than_swallowed(outcomes):
    """The opposite of the journal, and on purpose. Nothing is waiting on this answer, and
    the engine has the measurement written down and can send it again - so a 500 that
    makes it retry beats a success that loses one."""
    client, store = outcomes
    store.store.side_effect = RuntimeError("the database went away")

    response = post(client, example("outcomes-measured.json"))

    assert response.status_code == 500
    assert response.json()["error_code"] == "internal_error"


@pytest.mark.parametrize(
    "body",
    [
        {"outcomes": []},
        {"outcomes": [{"correlation_id": "c-1"}]},
        {},
    ],
    ids=["nothing to report", "missing fields", "no outcomes at all"],
)
def test_a_report_that_is_not_the_contract_is_refused_before_anything_is_stored(outcomes, body):
    client, store = outcomes

    response = post(client, body)

    assert response.status_code == 422
    assert response.json()["error_code"] == "invalid_request"
    store.store.assert_not_awaited()


def test_a_batch_larger_than_the_cap_is_refused(outcomes):
    """A request is a unit of work with a timeout, not a bulk load. A sweep with more to
    report sends more than one request."""
    client, store = outcomes
    one = example("outcomes-measured.json")["outcomes"][0]
    too_many = [one | {"correlation_id": f"c-{n}"} for n in range(MAX_OUTCOMES_PER_REQUEST + 1)]

    response = post(client, {"outcomes": too_many})

    assert response.status_code == 422
    store.store.assert_not_awaited()


def test_the_batch_cap_is_the_one_the_contract_states():
    """The other half of a constant that exists three times.

    500 is written here, in the engine's ReportOutcomesUseCase.MaxPerRequest, and in the
    contract. Each side used to test only its own copy, so the two could drift and both
    suites would stay green - and drift is not a slow recovery but a stop: the engine would
    send the same oversized batch every sweep, get a 422, mark nothing delivered and repeat.

    Binding both constants to the checked-in contract is what ties them to each other.
    """
    schema = json.loads((CONTRACTS / "outcome.schema.json").read_text(encoding="utf-8"))

    assert schema["properties"]["outcomes"]["maxItems"] == MAX_OUTCOMES_PER_REQUEST


def test_the_contract_and_the_model_require_the_same_fields():
    """Neither side generates the other, so this is what catches a field that became
    required in one place only."""
    schema = json.loads((CONTRACTS / "outcome.schema.json").read_text(encoding="utf-8"))
    required = set(schema["$defs"]["outcome"]["required"])

    model = OutcomeReport.model_json_schema()
    outcome = model["$defs"]["MeasuredOutcome"]

    assert required == set(outcome["required"])


def test_a_field_the_contract_does_not_have_is_refused_rather_than_ignored():
    body = example("outcomes-measured.json")
    body["outcomes"][0]["amount_usd"] = 1000

    with pytest.raises(ValidationError):
        OutcomeReport.model_validate(body)
