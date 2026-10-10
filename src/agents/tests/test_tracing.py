"""Exporting traces: off unless asked for, never fatal, and never carrying content.

The decision mirrors the engine's TelemetryExportExtensions, read from the same standard
variables, and ContentFreeExporter is the last thing every span passes before it leaves.
"""

import json
import time

import pytest
from opentelemetry.sdk.trace import ReadableSpan
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter
from opentelemetry.trace import StatusCode

from app.observability.tracing import (
    DEFAULT_SERVICE_NAME,
    Tracing,
    configure_tracing,
    content_free,
    decide,
)

# Stands in for anything an agent wrote. A marker rather than a real analysis, so a failing
# test leaks nothing either.
ANSWER_MARKER = "MARKER-ANSWER-91c2"


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


class TestOffUnlessAskedFor:
    @pytest.mark.parametrize("endpoint", [None, "", "   "])
    def test_without_an_endpoint_nothing_is_built(self, endpoint):
        environ = {} if endpoint is None else {"OTEL_EXPORTER_OTLP_ENDPOINT": endpoint}

        tracing = configure_tracing(environ)

        assert not tracing.enabled
        assert tracing.tracer is None
        assert "is not set" in tracing.description
        # No middleware at all on the agents, rather than one writing to a no-op provider.
        assert tracing.agent_middleware("risk_manager", provider_name="p", model_name="m") == ()

    @pytest.mark.parametrize("value", ["true", "TRUE", " True "])
    def test_otel_sdk_disabled_wins_over_an_endpoint(self, value):
        decision = decide(
            {"OTEL_EXPORTER_OTLP_ENDPOINT": "http://collector:4318", "OTEL_SDK_DISABLED": value}
        )

        assert not decision.enabled
        assert "OTEL_SDK_DISABLED" in decision.description

    @pytest.mark.parametrize("value", ["false", "", "no", "1"])
    def test_only_true_disables(self, value):
        decision = decide(
            {"OTEL_EXPORTER_OTLP_ENDPOINT": "http://collector:4318", "OTEL_SDK_DISABLED": value}
        )

        assert decision.enabled

    @pytest.mark.parametrize(
        "key",
        [
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
            "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
        ],
    )
    def test_a_per_signal_endpoint_turns_tracing_off_and_says_what_to_set(self, key):
        decision = decide(
            {"OTEL_EXPORTER_OTLP_ENDPOINT": "http://collector:4318", key: "http://elsewhere:4318"}
        )

        assert not decision.enabled
        assert key in decision.description
        assert "set OTEL_EXPORTER_OTLP_ENDPOINT instead" in decision.description

    @pytest.mark.parametrize(
        ("endpoint", "protocol"),
        [
            ("collector:4318", None),
            ("ftp://collector:4318", None),
            ("/v1/traces", None),
            ("http://collector:port", None),
            ("http://collector:4317", "grpc"),
            ("http://collector:4318", "thrift"),
        ],
    )
    def test_a_setting_the_exporter_cannot_use_is_reported_not_raised(self, endpoint, protocol):
        environ = {"OTEL_EXPORTER_OTLP_ENDPOINT": endpoint}
        if protocol:
            environ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol

        tracing = configure_tracing(environ)

        assert not tracing.enabled
        assert tracing.description.startswith("not exported")

    @pytest.mark.parametrize("protocol", [None, "http/protobuf"])
    def test_with_an_endpoint_traces_go_to_its_traces_path(self, protocol):
        environ = {"OTEL_EXPORTER_OTLP_ENDPOINT": "http://collector:4318/"}
        if protocol:
            environ["OTEL_EXPORTER_OTLP_PROTOCOL"] = protocol

        decision = decide(environ)

        assert decision.enabled
        assert decision.traces_endpoint == "http://collector:4318/v1/traces"
        assert decision.description == "exported over OTLP (http/protobuf) to http://collector:4318"

    def test_the_startup_line_never_repeats_credentials_from_the_url(self):
        decision = decide(
            {"OTEL_EXPORTER_OTLP_ENDPOINT": "https://someone:hunter2@collector.example/"}
        )

        assert decision.enabled
        assert "hunter2" not in decision.description
        assert "someone" not in decision.description
        assert decision.description.endswith("https://collector.example:443")


class TestContentFree:
    def test_an_exception_keeps_its_type_and_loses_its_message(self, exported):
        tracing = Tracing.to(exported, description="in memory", batch=False)
        assert tracing.tracer is not None

        with tracing.tracer.start_as_current_span("step") as span:
            span.set_attribute("gen_ai.agent.name", "risk_manager")
            span.set_attribute("gen_ai.input.messages", ANSWER_MARKER)
            span.set_attribute("something.new.in.ag2", ANSWER_MARKER)
            span.record_exception(ValueError(ANSWER_MARKER))
            span.set_status(StatusCode.ERROR, ANSWER_MARKER)

        (sent,) = exported.get_finished_spans()
        assert ANSWER_MARKER not in everything_in(sent)
        assert sent.attributes == {"gen_ai.agent.name": "risk_manager"}
        assert sent.status.status_code is StatusCode.ERROR
        assert [(e.name, dict(e.attributes)) for e in sent.events] == [
            ("exception", {"exception.type": "ValueError"})
        ]
        tracing.shutdown()

    def test_the_scrubbed_copy_keeps_the_trace_together(self, exported):
        tracing = Tracing.to(exported, description="in memory", batch=False)
        assert tracing.tracer is not None
        with tracing.tracer.start_as_current_span("parent"):
            with tracing.tracer.start_as_current_span("child"):
                pass

        child, parent = exported.get_finished_spans()
        assert child.parent.span_id == parent.context.span_id
        assert child.context.trace_id == parent.context.trace_id
        assert content_free(child).resource.attributes["service.name"] == DEFAULT_SERVICE_NAME
        tracing.shutdown()


class TestADeadCollector:
    @pytest.mark.parametrize(
        "endpoint",
        [
            "http://127.0.0.1:9",  # refuses at once
            "http://10.255.255.1:4318",  # never answers
        ],
    )
    def test_costs_a_stop_a_few_seconds_and_never_raises(self, endpoint):
        tracing = configure_tracing({"OTEL_EXPORTER_OTLP_ENDPOINT": endpoint})
        assert tracing.enabled and tracing.tracer is not None

        started = time.monotonic()
        for _ in range(3):
            with tracing.tracer.start_as_current_span("work"):
                pass
        # Spans end without waiting on the network: the batch processor exports elsewhere.
        assert time.monotonic() - started < 0.5

        started = time.monotonic()
        tracing.shutdown()
        # Inside Docker's ten seconds between SIGTERM and SIGKILL, with room.
        assert time.monotonic() - started < 8
