"""Who may call which endpoint.

/v1/signals starts an LLM run, /v1/outcomes writes into agent memory, and the market
endpoints spend provider quota. A single shared key that opens every door means a leak
anywhere is a leak of everything. Scopes keep a stolen signals key from forging outcomes,
and keep a market-only key from spending model time.

The legacy TAS_AGENT_API_KEY still grants every scope so existing deployments keep
working; scoped keys (TAS_AGENT_API_KEY_SIGNALS and friends) are the path forward.
Liveness and readiness stay open on purpose: a load balancer has to be able to ask
whether the service is up.
"""

from __future__ import annotations

import hmac
import logging
from collections.abc import Awaitable, Callable
from typing import Annotated

from fastapi import Depends, Header
from pydantic import SecretStr

from app.settings import Settings, get_settings

HEADER = "X-Api-Key"

logger = logging.getLogger(__name__)

SCOPE_SIGNALS_WRITE = "signals:write"
SCOPE_SCREEN_WRITE = "screen:write"
SCOPE_OUTCOMES_WRITE = "outcomes:write"
SCOPE_MARKET_READ = "market:read"

ALL_SCOPES = frozenset(
    {
        SCOPE_SIGNALS_WRITE,
        SCOPE_SCREEN_WRITE,
        SCOPE_OUTCOMES_WRITE,
        SCOPE_MARKET_READ,
    }
)


class NotAuthenticated(Exception):
    """No usable API key was presented, or it lacks the required scope."""

    error_code = "unauthorized"


def _optional_secret(value: SecretStr | None) -> bytes | None:
    if value is None:
        return None
    raw = value.get_secret_value()
    return raw.encode() if raw else None


def _key_entries(settings: Settings) -> list[tuple[bytes, frozenset[str]]]:
    """Every configured key and the scopes it carries.

    The legacy agent_api_key is full-access so a deployment that has not rotated yet
    keeps working. Optional scoped keys each grant exactly one scope.
    """
    entries: list[tuple[bytes, frozenset[str]]] = []

    legacy = settings.agent_api_key.get_secret_value().encode()
    if legacy:
        entries.append((legacy, ALL_SCOPES))

    scoped = (
        (getattr(settings, "agent_api_key_signals", None), SCOPE_SIGNALS_WRITE),
        (getattr(settings, "agent_api_key_screen", None), SCOPE_SCREEN_WRITE),
        (getattr(settings, "agent_api_key_outcomes", None), SCOPE_OUTCOMES_WRITE),
        (getattr(settings, "agent_api_key_market", None), SCOPE_MARKET_READ),
    )
    for secret, scope in scoped:
        key = _optional_secret(secret if isinstance(secret, SecretStr) else None)
        if key:
            entries.append((key, frozenset({scope})))

    return entries


def scopes_for_key(settings: Settings, supplied: str | None) -> frozenset[str] | None:
    """Returns the scopes for a matching key, or None when nothing matched.

    Every configured key is compared with compare_digest so a miss takes about as long
    as a hit. Equal-length compares are constant-time; unequal lengths return False from
    compare_digest without revealing which byte differed.
    """
    presented = supplied.encode() if supplied is not None else b""
    matched: frozenset[str] | None = None

    for expected, scopes in _key_entries(settings):
        if hmac.compare_digest(presented, expected):
            matched = scopes

    return matched


def require_scope(scope: str) -> Callable[..., Awaitable[None]]:
    """Dependency factory: the presented key must carry `scope`."""

    async def dependency(
        settings: Annotated[Settings, Depends(get_settings)],
        x_api_key: Annotated[str | None, Header()] = None,
    ) -> None:
        granted = scopes_for_key(settings, x_api_key)
        if granted is None or scope not in granted:
            raise NotAuthenticated

    return dependency


# Convenience bindings used on the router. Built once so Depends() identity is stable.
require_signals_write = require_scope(SCOPE_SIGNALS_WRITE)
require_screen_write = require_scope(SCOPE_SCREEN_WRITE)
require_outcomes_write = require_scope(SCOPE_OUTCOMES_WRITE)
require_market_read = require_scope(SCOPE_MARKET_READ)


async def require_api_key(
    settings: Annotated[Settings, Depends(get_settings)],
    x_api_key: Annotated[str | None, Header()] = None,
) -> None:
    """Rejects anything but a configured key. Kept for callers that need "any scope".

    Prefer require_scope on new routes so a market key cannot spend LLM time.
    """
    if scopes_for_key(settings, x_api_key) is None:
        raise NotAuthenticated
