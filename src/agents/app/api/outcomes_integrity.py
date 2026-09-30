"""Integrity check for POST /v1/outcomes.

The API key proves the caller may write outcomes; this HMAC proves the body was produced
by a holder of a separate shared secret. A stolen signals key alone must not be enough to
poison agent memory with forged hit/miss rows.
"""

from __future__ import annotations

import hashlib
import hmac
import logging
from typing import Annotated

from fastapi import Depends, Header, Request

from app.settings import Settings, get_settings

SIGNATURE_HEADER = "X-Outcomes-Signature"

logger = logging.getLogger(__name__)


class OutcomesIntegrityError(Exception):
    """The outcomes payload was missing a signature or the signature did not match."""

    error_code = "unauthorized"


def sign_outcomes_body(secret: str | bytes, body: bytes) -> str:
    """Returns the header value `sha256=<hex>` for the given body."""
    key = secret.encode() if isinstance(secret, str) else secret
    digest = hmac.new(key, body, hashlib.sha256).hexdigest()
    return f"sha256={digest}"


async def require_outcomes_hmac(
    request: Request,
    settings: Annotated[Settings, Depends(get_settings)],
    x_outcomes_signature: Annotated[str | None, Header()] = None,
) -> None:
    """Fail closed: missing, malformed or wrong signatures are all the same refusal."""
    secret = settings.outcomes_hmac_secret.get_secret_value().encode()
    body = await request.body()
    expected = sign_outcomes_body(secret, body)
    provided = x_outcomes_signature or ""

    if not hmac.compare_digest(provided.encode(), expected.encode()):
        logger.warning("Rejected outcomes request with missing or invalid HMAC signature")
        raise OutcomesIntegrityError
