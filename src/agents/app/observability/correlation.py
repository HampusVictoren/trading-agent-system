"""One id per request, carried through every log line and every error body.

The id is what ties a line in the engine's log to the analysis that produced it. It is
read from the request when the caller supplies one, so a whole chain shares it.
"""

import re
import uuid
from contextvars import ContextVar

from starlette.datastructures import Headers, MutableHeaders
from starlette.types import ASGIApp, Message, Receive, Scope, Send

HEADER = "X-Correlation-Id"

_correlation_id: ContextVar[str] = ContextVar("correlation_id", default="-")

# The id goes straight into a log line, so only a short, printable token is accepted.
# Anything else is replaced rather than rejected: a caller with a strange header should
# still get its analysis, just not get to write into the log.
_ACCEPTABLE = re.compile(r"^[A-Za-z0-9._-]{1,64}$")


def current_correlation_id() -> str:
    return _correlation_id.get()


class CorrelationIdMiddleware:
    """Plain ASGI middleware on purpose.

    BaseHTTPMiddleware runs the endpoint in a separate task, and a ContextVar set in its
    dispatch does not reliably reach the endpoint. This runs in the same task, so it does.
    """

    def __init__(self, app: ASGIApp) -> None:
        self.app = app

    async def __call__(self, scope: Scope, receive: Receive, send: Send) -> None:
        if scope["type"] != "http":
            await self.app(scope, receive, send)
            return

        supplied = Headers(scope=scope).get(HEADER)
        correlation_id = supplied if supplied and _ACCEPTABLE.match(supplied) else str(uuid.uuid4())

        async def send_with_header(message: Message) -> None:
            if message["type"] == "http.response.start":
                MutableHeaders(scope=message).append(HEADER, correlation_id)
            await send(message)

        token = _correlation_id.set(correlation_id)
        try:
            await self.app(scope, receive, send_with_header)
        finally:
            _correlation_id.reset(token)
