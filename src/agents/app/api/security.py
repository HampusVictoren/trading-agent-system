"""Who may ask for an analysis.

/analyze starts an LLM run, so an unauthenticated one is a way for anything that can
reach the port to spend the machine's time. The liveness and readiness probes stay open
on purpose: a load balancer has to be able to ask whether the service is up.
"""

import hmac
import logging
from typing import Annotated

from fastapi import Depends, Header

from app.settings import Settings, get_settings

HEADER = "X-Api-Key"

logger = logging.getLogger(__name__)


class NotAuthenticated(Exception):
    """No usable API key was presented."""

    error_code = "unauthorized"


async def require_api_key(
    settings: Annotated[Settings, Depends(get_settings)],
    x_api_key: Annotated[str | None, Header()] = None,
) -> None:
    """Rejects anything but the configured key.

    compare_digest takes the same time whichever byte differs first. A plain == returns
    as soon as it finds a difference, which leaks the key one character at a time to
    anyone who can measure the difference.

    A missing key and a wrong key give the same answer, so the response says nothing
    about which of the two it was.
    """
    expected = settings.agent_api_key.get_secret_value().encode()
    supplied = x_api_key.encode() if x_api_key is not None else b""

    if not hmac.compare_digest(supplied, expected):
        raise NotAuthenticated
