"""Traces for the agent service, exported over OTLP only when somebody asked for them.

The engine opens one trace per cycle and sends its context in a W3C `traceparent` header on
every call here. This module is the receiving half: it decides at startup whether anything is
exported at all, and owns the one `TracerProvider` the service's own server spans and AG2's
`TelemetryMiddleware` both write to - so the agents' work lands in the engine's trace.

**Off unless asked for, and never fatal.** The same rules as the engine's
`TelemetryExportExtensions`, read from the same standard variables:

- `OTEL_EXPORTER_OTLP_ENDPOINT` unset or blank: no provider, no span, nothing listening.
- `OTEL_SDK_DISABLED=true`: off, whatever the endpoint says.
- A per-signal endpoint (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` and its siblings): off, with a
  line saying to set the general one - otherwise the exporter would send traces somewhere
  other than where the startup line says.
- An endpoint that is not an absolute http(s) URL, or a protocol other than `http/protobuf`:
  off, and said once. `grpc` is not supported here, because it would pull grpcio into an
  image that has no other use for it; the collector in compose takes both.

None of these refuses to start. Telemetry is not a reason to stop the service the engine's
analyses depend on.

**Nothing an agent wrote, or was given, leaves the process.** `TelemetryMiddleware` runs with
`capture_content=False`, and on top of that every span passes through `ContentFreeExporter`
on its way out, which keeps an allowlist of attributes and drops every exception message: AG2
records `str(exc)` as a span's status, and a validation error quotes the model's answer.

Not here: logs (they stay on stdout, decided with stage 7's PR 3), metrics, and any
instrumentation of the HTTP clients this service uses - the LLM call is AG2's `chat` span.
The global tracer provider is never set, so no library can start emitting by accident.
"""

import logging
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Final
from urllib.parse import urlsplit

from ag2.middleware.base import MiddlewareFactory
from ag2.middleware.builtin.telemetry import TelemetryMiddleware
from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import Event, ReadableSpan, TracerProvider
from opentelemetry.sdk.trace.export import (
    BatchSpanProcessor,
    SimpleSpanProcessor,
    SpanExporter,
    SpanExportResult,
)
from opentelemetry.trace import Link, Status, Tracer

logger = logging.getLogger(__name__)

ENDPOINT_KEY: Final = "OTEL_EXPORTER_OTLP_ENDPOINT"
PROTOCOL_KEY: Final = "OTEL_EXPORTER_OTLP_PROTOCOL"
SERVICE_NAME_KEY: Final = "OTEL_SERVICE_NAME"
TIMEOUT_KEY: Final = "OTEL_EXPORTER_OTLP_TIMEOUT"
DISABLED_KEY: Final = "OTEL_SDK_DISABLED"
PER_SIGNAL_ENDPOINT_KEYS: Final = (
    "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
    "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
    "OTEL_EXPORTER_OTLP_LOGS_ENDPOINT",
)

DEFAULT_SERVICE_NAME: Final = "agents"
SUPPORTED_PROTOCOL: Final = "http/protobuf"
TRACES_PATH: Final = "/v1/traces"

# Seconds per export unless OTEL_EXPORTER_OTLP_TIMEOUT says otherwise - the engine's three,
# for the same reason: a collector on the same host answers in milliseconds, and one that is
# gone should cost a stop a few seconds, not the SDK's ten. This is also the only bound on a
# stop: the batch processor's own export_timeout_millis is accepted and unused by this SDK,
# and its shutdown waits up to thirty seconds for an export in flight - so the exporter's
# timeout is what keeps a stop inside Docker's ten seconds before SIGKILL.
DEFAULT_TIMEOUT_S: Final = 3.0

# AG2's switch for putting prompts, answers and tool arguments on spans. Off, and not a
# setting: a thesis, a fact sheet or a prompt in a trace is a copy of the analysis outside
# the database that holds it, under no retention rule. ContentFreeExporter below enforces the
# same thing a second time, so turning this on would still export nothing.
CAPTURE_CONTENT: Final = False


@dataclass(frozen=True)
class TracingDecision:
    """What startup decided, so it can be said once in the log."""

    enabled: bool
    description: str
    traces_endpoint: str | None = None


def decide(environ: Mapping[str, str]) -> TracingDecision:
    """Reads the standard variables. Every refusal is a reason, never an exception."""
    if environ.get(DISABLED_KEY, "").strip().lower() == "true":
        return TracingDecision(False, f"not exported: {DISABLED_KEY} is true")

    for key in PER_SIGNAL_ENDPOINT_KEYS:
        if environ.get(key, "").strip():
            return TracingDecision(
                False,
                f"not exported: {key} is set, and per-signal endpoints are not supported - "
                f"set {ENDPOINT_KEY} instead",
            )

    endpoint = environ.get(ENDPOINT_KEY, "").strip()
    if not endpoint:
        return TracingDecision(False, f"not exported: {ENDPOINT_KEY} is not set")

    not_a_url = TracingDecision(
        False, f"not exported: {ENDPOINT_KEY} is not an absolute http(s) URL"
    )
    try:
        parts = urlsplit(endpoint)
        explicit_port = parts.port  # raises on a port that is not a number
    except ValueError:
        return not_a_url
    if parts.scheme not in ("http", "https") or not parts.hostname:
        return not_a_url

    protocol = environ.get(PROTOCOL_KEY, "").strip() or SUPPORTED_PROTOCOL
    if protocol != SUPPORTED_PROTOCOL:
        return TracingDecision(
            False, f"not exported: {PROTOCOL_KEY} must be {SUPPORTED_PROTOCOL} for this service"
        )

    # Scheme, host and port only: a URL's user-info can carry a credential, and this line is
    # logged.
    port = explicit_port or (443 if parts.scheme == "https" else 80)
    return TracingDecision(
        True,
        f"exported over OTLP ({protocol}) to {parts.scheme}://{parts.hostname}:{port}",
        traces_endpoint=endpoint.rstrip("/") + TRACES_PATH,
    )


# What a span may carry out of the process. An allowlist rather than a list of what to
# drop, so an attribute a future AG2 adds is withheld until somebody has read what it holds.
_ALLOWED_ATTRIBUTES: Final = frozenset(
    {
        # AG2's TelemetryMiddleware, without content: which agent, which model, how it ended.
        "ag2.span.type",
        "ag2.usage.kind",
        "ag2.usage.total_tokens",
        "gen_ai.operation.name",
        "gen_ai.agent.name",
        "gen_ai.provider.name",
        "gen_ai.request.model",
        "gen_ai.response.model",
        "gen_ai.response.finish_reasons",
        "gen_ai.tool.name",
        "gen_ai.tool.call.id",
        "gen_ai.tool.type",
        # This service's server span.
        "http.request.method",
        "http.route",
        "http.response.status_code",
        "url.path",
        "error.type",
        "trading.correlation_id",
    }
)
_ALLOWED_PREFIXES: Final = ("gen_ai.usage.",)

# The exception event keeps its type. Its message and stack trace go: a pydantic error quotes
# the input it refused, which here is the model's answer.
_EXCEPTION_EVENT: Final = "exception"
_EXCEPTION_TYPE: Final = "exception.type"


def _allowed(key: str) -> bool:
    return key in _ALLOWED_ATTRIBUTES or key.startswith(_ALLOWED_PREFIXES)


def content_free(span: ReadableSpan) -> ReadableSpan:
    """The same span with only what is known to carry no content."""
    events = [
        Event(
            _EXCEPTION_EVENT,
            {_EXCEPTION_TYPE: event.attributes[_EXCEPTION_TYPE]}
            if event.attributes and _EXCEPTION_TYPE in event.attributes
            else {},
            event.timestamp,
        )
        for event in span.events
        if event.name == _EXCEPTION_EVENT
    ]
    return ReadableSpan(
        name=span.name,
        context=span.context,
        parent=span.parent,
        resource=span.resource,
        attributes={key: value for key, value in (span.attributes or {}).items() if _allowed(key)},
        events=events,
        links=[Link(link.context) for link in span.links],
        kind=span.kind,
        # The code, never the description: AG2 sets it to str(exc).
        status=Status(span.status.status_code),
        start_time=span.start_time,
        end_time=span.end_time,
        instrumentation_scope=span.instrumentation_scope,
    )


class ContentFreeExporter(SpanExporter):
    """Wraps the real exporter and hands it content-free copies, whatever produced them."""

    def __init__(self, inner: SpanExporter) -> None:
        self._inner = inner

    def export(self, spans: Sequence[ReadableSpan]) -> SpanExportResult:
        return self._inner.export([content_free(span) for span in spans])

    def shutdown(self) -> None:
        self._inner.shutdown()

    def force_flush(self, timeout_millis: int = 30_000) -> bool:
        return self._inner.force_flush(timeout_millis)


INSTRUMENTATION_NAME: Final = "app"


class Tracing:
    """The service's tracing, or the absence of it. Built once, at startup."""

    def __init__(self, provider: TracerProvider | None, description: str) -> None:
        self._provider = provider
        self.description = description
        self.tracer: Tracer | None = (
            provider.get_tracer(INSTRUMENTATION_NAME) if provider is not None else None
        )

    @property
    def enabled(self) -> bool:
        return self._provider is not None

    @classmethod
    def disabled(cls, description: str = "not exported") -> "Tracing":
        return cls(None, description)

    @classmethod
    def to(
        cls,
        exporter: SpanExporter,
        *,
        description: str,
        service_name: str = DEFAULT_SERVICE_NAME,
        batch: bool = True,
    ) -> "Tracing":
        """A provider whose every span goes through ContentFreeExporter to `exporter`."""
        provider = TracerProvider(
            resource=Resource.create({"service.name": service_name}),
            # Its own shutdown, from the lifespan; not an atexit hook racing uvicorn's.
            shutdown_on_exit=False,
        )
        guarded = ContentFreeExporter(exporter)
        provider.add_span_processor(
            BatchSpanProcessor(guarded) if batch else SimpleSpanProcessor(guarded)
        )
        return cls(provider, description)

    def agent_middleware(
        self, role: str, *, provider_name: str | None, model_name: str | None
    ) -> tuple[MiddlewareFactory, ...]:
        """AG2's TelemetryMiddleware for one agent, or nothing at all when tracing is off."""
        if self._provider is None:
            return ()
        return (
            TelemetryMiddleware(
                tracer_provider=self._provider,
                capture_content=CAPTURE_CONTENT,
                agent_name=role,
                provider_name=provider_name,
                model_name=model_name,
            ),
        )

    def shutdown(self) -> None:
        """Flushes what is queued and stops, bounded by the exporter's timeout."""
        if self._provider is not None:
            self._provider.shutdown()


def configure_tracing(environ: Mapping[str, str]) -> Tracing:
    decision = decide(environ)
    if not decision.enabled:
        return Tracing.disabled(decision.description)

    service_name = environ.get(SERVICE_NAME_KEY, "").strip() or DEFAULT_SERVICE_NAME
    # The endpoint is passed rather than left to the exporter, so what is used is what the
    # startup line names; headers and compression it still reads from the environment itself.
    timeout = None if environ.get(TIMEOUT_KEY, "").strip() else DEFAULT_TIMEOUT_S
    exporter = OTLPSpanExporter(endpoint=decision.traces_endpoint, timeout=timeout)
    return Tracing.to(exporter, description=decision.description, service_name=service_name)
