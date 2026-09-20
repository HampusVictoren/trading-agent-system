"""Turns an expected failure into an honest status code.

Nothing here puts str(exception) in a response. The detail is logged on the server, with
the correlation id; the caller gets a stable error_code and that same id, so the two can
be matched up without the service telling a stranger about its internals.
"""

import logging

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.application.errors import (
    AgentChainFailed,
    AgentResponseInvalid,
    AnalysisError,
    LlmFailed,
    LlmTimeout,
    LlmUnreachable,
)
from app.observability.correlation import current_correlation_id

logger = logging.getLogger(__name__)

# 502: the backend answered, but not usefully. 503: it could not be reached at all.
# 504: it accepted the request and never answered.
STATUS_BY_ERROR: dict[type[AnalysisError], int] = {
    LlmTimeout: 504,
    LlmUnreachable: 503,
    LlmFailed: 502,
    AgentResponseInvalid: 502,
    AgentChainFailed: 502,
}


def _body(error_code: str) -> dict[str, str]:
    return {"error_code": error_code, "correlation_id": current_correlation_id()}


async def handle_analysis_error(request: Request, exc: Exception) -> JSONResponse:
    # Starlette types every handler against Exception. This one is registered only for
    # AnalysisError, so anything else is a wiring bug and must not be swallowed here.
    if not isinstance(exc, AnalysisError):
        raise exc

    status_code = STATUS_BY_ERROR.get(type(exc), 500)
    logger.warning("Analysis failed with %s: %s", exc.error_code, exc)
    return JSONResponse(status_code=status_code, content=_body(exc.error_code))


async def handle_invalid_request(request: Request, exc: Exception) -> JSONResponse:
    logger.info("Rejected an invalid request: %s", exc)
    return JSONResponse(status_code=422, content=_body("invalid_request"))


async def handle_unexpected(request: Request, exc: Exception) -> JSONResponse:
    # Only a bug reaches this point, so it is logged with its stack trace.
    logger.exception("Unexpected failure while handling %s", request.url.path)
    return JSONResponse(status_code=500, content=_body("internal_error"))


def register_error_handlers(app: FastAPI) -> None:
    app.add_exception_handler(AnalysisError, handle_analysis_error)
    app.add_exception_handler(RequestValidationError, handle_invalid_request)
    app.add_exception_handler(Exception, handle_unexpected)
