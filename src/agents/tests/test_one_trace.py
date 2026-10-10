"""One trace with the engine's: the server span, every agent inside it, and no content.

The in-process proof that an engine cycle and the agents' work are one trace: a request that
carries the engine's `traceparent` reaches the real route, the real pipeline and the real AG2
agents over a scripted model, and every span that comes out is in the engine's trace, with
the server span a child of the engine's call. The same run carries marker text in the fact
sheet and in every answer the model gives, and none of it may appear in what is exported.
"""

import json
import threading
from types import SimpleNamespace
from unittest.mock import AsyncMock

import pytest
from ag2.testing import TestConfig
from fastapi.testclient import TestClient
from opentelemetry import trace
from opentelemetry.sdk.trace import ReadableSpan
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from opentelemetry.trace import SpanKind

from app.api.security import HEADER as API_KEY_HEADER
from app.application.pipeline import SignalPipeline, TeamRuntime
from app.application.teams import DEFAULT_TEAM, load_prompts
from app.dependencies import get_resources
from app.domain.facts import MarketSnapshot, Quote
from app.infrastructure.ag2.runner import Ag2StepRunner, build_agents
from app.infrastructure.llm.provider import ModelConfigs
from app.main import create_app
from app.observability.tracing import (
    CAPTURE_CONTENT,
    Tracing,
)
from app.settings import get_settings
from tests.api_support import API_KEY, api_settings
from tests.test_pipeline import RecallingMemory, RecordingJournal, StubMarket

# Text that stands in for what must never leave the process: the facts the agents are given,
# and what they answer. Marker strings rather than real analyses, so a failing test leaks
# nothing either.
FACT_MARKER = "MARKER-FACT-7f3a"
ANSWER_MARKER = "MARKER-ANSWER-91c2"
MARKERS = (FACT_MARKER, ANSWER_MARKER)

ENGINE_TRACE_ID = "4bf92f3577b34da6a3ce929d0e0e4736"
ENGINE_SPAN_ID = "00f067aa0ba902b7"
TRACEPARENT = f"00-{ENGINE_TRACE_ID}-{ENGINE_SPAN_ID}-01"

A_REQUEST = {
    "instrument": {"type": "equity", "symbol": "AAPL"},
    "team_id": "default",
    "as_of": "2026-09-23T14:02:55Z",
    "existing_position": None,
    "available_risk_budget": 500.0,
    "max_position_pct": 0.05,
    "correlation_id": "0f2d7c11-3b48-4e9a-8c15-77ab2e4d6f30",
}

ANSWERS = {
    "market_analyst": {"trend": "UP", "valuation": "FAIR", "observations": [ANSWER_MARKER]},
    "risk_manager": {"downside": "MEDIUM", "veto": False, "risks": [ANSWER_MARKER]},
    "portfolio_manager": {
        "stance": "BUY",
        "conviction": 0.7,
        "thesis": ANSWER_MARKER,
        "key_risks": [ANSWER_MARKER],
        "horizon_days": 5,
    },
}


class MarkedMarket(StubMarket):
    """A fact sheet whose sector is the marker, so it is in every step's prompt."""

    async def snapshot(self, symbol: str) -> MarketSnapshot:
        snapshot = await super().snapshot(symbol)
        return MarketSnapshot(
            quote=Quote(**(snapshot.quote.model_dump() | {"sector": FACT_MARKER})),
            history=snapshot.history,
        )


def everything_in(span: ReadableSpan) -> str:
    """Every string an exporter would send for this span, as one text to search."""
    return json.dumps(
        {
            "name": span.name,
            "attributes": dict(span.attributes or {}),
            "events": [(e.name, dict(e.attributes or {})) for e in span.events],
            "status": span.status.description,
            "links": [dict(link.attributes or {}) for link in span.links],
        },
        default=str,
        ensure_ascii=False,
    )


@pytest.fixture
def exported():
    return InMemorySpanExporter()


@pytest.fixture
def traced_service(exported):
    """The real route, pipeline and AG2 agents, a scripted model, and spans kept in memory."""
    tracing = Tracing.to(exported, description="in memory", batch=False)

    def build(answers=ANSWERS, repeat: int = 1):
        def middleware_for(role: str):
            return tracing.agent_middleware(role, provider_name="test", model_name="scripted")

        configs = ModelConfigs(
            default=TestConfig("{}"),
            by_role={
                role: TestConfig(*[json.dumps(answer)] * repeat) for role, answer in answers.items()
            },
        )
        agents = build_agents(DEFAULT_TEAM, load_prompts(DEFAULT_TEAM), configs, middleware_for)
        pipeline = SignalPipeline(
            {
                DEFAULT_TEAM.id: TeamRuntime(
                    spec=DEFAULT_TEAM, version="abc123def456", runner=Ag2StepRunner(agents)
                )
            },
            MarkedMarket(),
            RecordingJournal(),
            RecallingMemory(),
        )

        app = create_app(tracing=tracing)
        app.dependency_overrides[get_settings] = lambda: api_settings()
        app.dependency_overrides[get_resources] = lambda: SimpleNamespace(
            pipeline=pipeline,
            models=None,
            memory=SimpleNamespace(ping=AsyncMock()),
            http_client=SimpleNamespace(get=AsyncMock()),
            llm_base_url=None,
        )
        return TestClient(app, raise_server_exceptions=False, headers={API_KEY_HEADER: API_KEY})

    yield build
    tracing.shutdown()


def by_name(spans, prefix: str) -> list[ReadableSpan]:
    return [span for span in spans if span.name.startswith(prefix)]


class TestOneTraceWithTheEngine:
    def test_the_agents_work_is_inside_the_engines_trace(self, traced_service, exported):
        response = traced_service().post(
            "/v1/signals", json=A_REQUEST, headers={"traceparent": TRACEPARENT}
        )
        assert response.status_code == 200
        # The markers did reach the agents and come back: the test is not passing because
        # nothing was there to leak.
        assert response.json()["thesis"] == ANSWER_MARKER

        spans = exported.get_finished_spans()
        (server,) = [span for span in spans if span.kind is SpanKind.SERVER]
        agents = by_name(spans, "invoke_agent ")
        chats = by_name(spans, "chat ")

        # One trace: the engine's.
        assert {format(span.context.trace_id, "032x") for span in spans} == {ENGINE_TRACE_ID}
        # The server span continues the engine's call to it ...
        assert server.parent is not None and server.parent.is_remote
        assert format(server.parent.span_id, "016x") == ENGINE_SPAN_ID
        # ... and every agent of the team runs inside it, each with its model call inside.
        assert [span.attributes["gen_ai.agent.name"] for span in agents] == list(DEFAULT_TEAM.roles)
        assert {span.parent.span_id for span in agents} == {server.context.span_id}
        agent_ids = {span.context.span_id for span in agents}
        assert len(chats) == len(agents)
        assert all(span.parent.span_id in agent_ids for span in chats)
        # All of it through the service's own provider: none was installed globally, so no
        # library this service uses can start emitting spans of its own.
        assert isinstance(trace.get_tracer_provider(), trace.ProxyTracerProvider)

    def test_the_server_span_says_what_was_called_and_by_which_analysis(
        self, traced_service, exported
    ):
        # Sent as a header by the engine, beside the traceparent.
        traced_service().post(
            "/v1/signals",
            json=A_REQUEST,
            headers={"traceparent": TRACEPARENT, "X-Correlation-Id": A_REQUEST["correlation_id"]},
        )

        (server,) = [s for s in exported.get_finished_spans() if s.kind is SpanKind.SERVER]
        assert server.name == "POST /v1/signals"
        assert server.attributes["http.route"] == "/v1/signals"
        assert server.attributes["http.response.status_code"] == 200
        # The engine's correlation id, which also joins the trace to both services' logs.
        assert server.attributes["trading.correlation_id"] == A_REQUEST["correlation_id"]

    def test_without_a_traceparent_the_request_is_a_trace_of_its_own(
        self, traced_service, exported
    ):
        traced_service().post("/v1/signals", json=A_REQUEST)

        spans = exported.get_finished_spans()
        (server,) = [span for span in spans if span.kind is SpanKind.SERVER]
        assert server.parent is None
        assert {span.context.trace_id for span in spans} == {server.context.trace_id}

    def test_a_malformed_traceparent_starts_a_new_trace_rather_than_failing(
        self, traced_service, exported
    ):
        response = traced_service().post(
            "/v1/signals", json=A_REQUEST, headers={"traceparent": "00-not-a-trace-01"}
        )

        assert response.status_code == 200
        (server,) = [s for s in exported.get_finished_spans() if s.kind is SpanKind.SERVER]
        assert server.parent is None

    def test_a_path_that_matches_no_route_is_not_recorded(self, traced_service, exported):
        response = traced_service().get(
            "/v1/MARKER-PATH-3e1d/../etc", headers={"traceparent": TRACEPARENT}
        )

        assert response.status_code == 404
        (server,) = [s for s in exported.get_finished_spans() if s.kind is SpanKind.SERVER]
        assert server.name == "GET"
        assert "http.route" not in server.attributes
        assert server.attributes["http.response.status_code"] == 404
        assert "MARKER-PATH" not in everything_in(server)

    def test_the_probes_are_not_traced(self, traced_service, exported):
        client = traced_service()
        client.get("/health")

        assert exported.get_finished_spans() == ()


class TestNothingTheAgentsSawOrSaidLeaves:
    def test_capture_content_is_off(self):
        assert CAPTURE_CONTENT is False

    def test_a_whole_analysis_exports_no_fact_and_no_answer(self, traced_service, exported):
        traced_service().post("/v1/signals", json=A_REQUEST, headers={"traceparent": TRACEPARENT})

        spans = exported.get_finished_spans()
        assert spans
        for span in spans:
            text = everything_in(span)
            assert not any(marker in text for marker in MARKERS), span.name

    def test_a_failed_analysis_exports_no_answer_either(self, traced_service, exported):
        # A pydantic error quotes the input it refused - here, the model's answer - and AG2
        # puts str(exc) on the span. Every answer is wrong, so every retry fails with it.
        wrong = dict(ANSWERS) | {
            "market_analyst": {"trend": ANSWER_MARKER, "valuation": "FAIR", "observations": []}
        }
        response = traced_service(answers=wrong, repeat=5).post(
            "/v1/signals", json=A_REQUEST, headers={"traceparent": TRACEPARENT}
        )

        assert response.status_code >= 400
        spans = exported.get_finished_spans()
        assert spans
        for span in spans:
            text = everything_in(span)
            assert not any(marker in text for marker in MARKERS), span.name


class TestTracingOff:
    def test_a_service_with_tracing_off_traces_nothing_and_sets_no_global_provider(self, exported):
        app = create_app()
        app.dependency_overrides[get_settings] = lambda: api_settings()
        TestClient(app, raise_server_exceptions=False).get(
            "/v1/history/AAPL", headers={"traceparent": TRACEPARENT}
        )

        # Nothing this service does may install a global provider, even with tracing on;
        # with it off there is not even a private one.
        assert isinstance(trace.get_tracer_provider(), trace.ProxyTracerProvider)
        assert app.state.tracing_slot.current is None


class TestFastApisOwnTelemetryStaysOff:
    """FastAPI 0.142 exports traces, metrics and logs by itself when it sees an OTLP endpoint and
    the SDK. Run through the real ASGI lifespan, where it would do so, with an endpoint set."""

    def test_an_endpoint_in_the_environment_installs_no_global_provider(self, monkeypatch):
        from contextlib import asynccontextmanager

        from opentelemetry import _logs, metrics

        import app.main as main

        @asynccontextmanager
        async def no_resources(app, settings, tracing):
            yield

        monkeypatch.setattr(main, "get_settings", lambda: api_settings())
        monkeypatch.setattr(main, "_serve", no_resources)
        monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
        monkeypatch.delenv("OTEL_SDK_DISABLED", raising=False)

        app = main.create_app()
        with TestClient(app) as client:
            assert app.state.tracing_slot.current.enabled
            client.get("/health")

        assert isinstance(trace.get_tracer_provider(), trace.ProxyTracerProvider)
        assert type(metrics.get_meter_provider()).__name__ == "_ProxyMeterProvider"
        assert type(_logs.get_logger_provider()).__name__ == "ProxyLoggerProvider"


class TestTheLifespanDecides:
    """Startup reads the environment once, puts the result where the middleware looks, and
    shuts the provider down on the way out. Resources are stubbed: this is about tracing."""

    @pytest.fixture
    def started(self, monkeypatch):
        from contextlib import asynccontextmanager

        import app.main as main

        @asynccontextmanager
        async def no_resources(app, settings, tracing):
            yield

        monkeypatch.setattr(main, "get_settings", lambda: api_settings())
        monkeypatch.setattr(main, "_serve", no_resources)
        for key in ("OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_SDK_DISABLED"):
            monkeypatch.delenv(key, raising=False)
        return main

    async def test_an_endpoint_in_the_environment_turns_tracing_on_for_the_service(
        self, started, monkeypatch
    ):
        monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
        shut_down: list[Tracing] = []
        real = started.configure_tracing

        shut_down_on: list[threading.Thread] = []

        def watched(environ):
            tracing = real(environ)
            original = tracing.shutdown

            def recorded():
                shut_down.append(tracing)
                shut_down_on.append(threading.current_thread())
                original()

            monkeypatch.setattr(tracing, "shutdown", recorded)
            return tracing

        monkeypatch.setattr(started, "configure_tracing", watched)
        app = started.create_app()
        slot = app.state.tracing_slot

        async with started.lifespan(app):
            loop_thread = threading.current_thread()
            tracing = slot.current
            assert tracing is not None and tracing.enabled
            assert shut_down == []

        assert slot.current is None
        assert shut_down == [tracing]
        # Off the event loop: the flush can block for the exporter's whole timeout.
        assert shut_down_on != [loop_thread]

    async def test_without_one_the_service_runs_untraced(self, started):
        app = started.create_app()

        async with started.lifespan(app):
            tracing = app.state.tracing_slot.current
            assert tracing is not None and not tracing.enabled

    async def test_otel_sdk_disabled_in_the_environment_is_honoured(self, started, monkeypatch):
        monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:9")
        monkeypatch.setenv("OTEL_SDK_DISABLED", "true")
        app = started.create_app()

        async with started.lifespan(app):
            assert not app.state.tracing_slot.current.enabled
