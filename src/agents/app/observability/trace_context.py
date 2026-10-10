"""The server span: one per request, continuing the caller's trace when it sent one.

The engine writes a W3C `traceparent` on every call, carrying the id of its cycle's trace and
of the HTTP client span that made the call. This middleware extracts it and opens the
request's span as that span's child, current for the whole request - so AG2's agent and LLM
spans, opened inside the endpoint, are children of it, and an engine cycle and the agents'
work end up in one trace.

Plain ASGI, like CorrelationIdMiddleware and for the same reason: the span has to be current
in the task the endpoint runs in, and BaseHTTPMiddleware runs it in another.

Only `traceparent` is read. `tracestate` and baggage are not: neither is sent by the engine,
and baggage is caller-chosen key-value data this service would otherwise carry onwards. A
`traceparent` from a caller that is not the engine names a parent span and nothing more; it
cannot make the service do anything, and the request is authenticated after this as before.

No headers, no query string and no body are recorded. The probes `/health` and `/ready` get
no span: a container healthcheck calls them every few seconds, and a trace of each would bury
the ones that matter.
"""

from opentelemetry.trace import SpanKind, Status, StatusCode
from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator
from starlette.datastructures import Headers
from starlette.types import ASGIApp, Message, Receive, Scope, Send

from app.observability.correlation import current_correlation_id
from app.observability.tracing import Tracing

TRACEPARENT = "traceparent"
UNTRACED_PATHS = frozenset({"/health", "/ready"})

_propagator = TraceContextTextMapPropagator()


class TracingSlot:
    """Where the lifespan puts the service's Tracing, for a middleware built before it runs.

    Starlette builds the middleware stack when the app first starts, from the class and its
    arguments; the tracing is decided in the lifespan, from the environment. Until it is set,
    nothing is traced - unless a test handed the app a Tracing of its own, which is current
    from the start because such tests do not run the lifespan.
    """

    def __init__(self, preset: Tracing | None = None) -> None:
        self.preset = preset
        self.current: Tracing | None = preset


class TraceContextMiddleware:
    def __init__(self, app: ASGIApp, slot: TracingSlot) -> None:
        self.app = app
        self.slot = slot

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        tracing = self.slot.current
        if (
            scope["type"] != "http"
            or tracing is None
            or tracing.tracer is None
            or scope["path"] in UNTRACED_PATHS
        ):
            await self.app(scope, receive, send)
            return

        supplied = Headers(scope=scope).get(TRACEPARENT)
        # An invalid header extracts to an empty context, so the span becomes a root.
        parent = _propagator.extract({TRACEPARENT: supplied}) if supplied else None
        method = scope["method"]
        status: dict[str, int] = {}

        async def send_recording_status(message: Message) -> None:
            if message["type"] == "http.response.start":
                status["code"] = message["status"]
            await send(message)

        with tracing.tracer.start_as_current_span(
            method,
            context=parent,
            kind=SpanKind.SERVER,
            # Recorded below by type only; the SDK's own would store the message.
            record_exception=False,
            set_status_on_exception=False,
        ) as span:
            span.set_attribute("http.request.method", method)
            span.set_attribute("url.path", scope["path"])
            span.set_attribute("trading.correlation_id", current_correlation_id())
            try:
                await self.app(scope, receive, send_recording_status)
            except Exception as error:
                span.set_attribute("error.type", type(error).__qualname__)
                span.set_status(Status(StatusCode.ERROR))
                raise
            finally:
                # The route template, which FastAPI puts in the scope once it has matched -
                # `/v1/history/{symbol}` rather than one span name per ticker.
                route = getattr(scope.get("route"), "path", None)
                if route:
                    span.set_attribute("http.route", route)
                    span.update_name(f"{method} {route}")
                if "code" in status:
                    span.set_attribute("http.response.status_code", status["code"])
                    if status["code"] >= 500:
                        span.set_status(Status(StatusCode.ERROR))
