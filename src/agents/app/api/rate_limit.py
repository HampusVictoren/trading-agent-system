"""Per-key and global rate limits for the costly endpoints.

POST /v1/signals starts an LLM chain; POST /v1/screen fetches a universe of bars.
An authenticated caller who can hammer either endpoint can burn model time or market-data
quota without ever looking like an attacker. These limits are the thin budget that keeps
one key - or the whole process - from doing that.

In-process token buckets are enough for a single service instance on loopback. They are
not a distributed quota; a multi-replica deployment needs a shared store.
"""

from __future__ import annotations

import logging
import threading
import time
from collections.abc import Awaitable, Callable
from typing import Annotated

from fastapi import Depends, Header, Request

from app.settings import Settings, get_settings

logger = logging.getLogger(__name__)

HEADER = "X-Api-Key"


class RateLimited(Exception):
    """The caller has spent this endpoint's budget for the window."""

    error_code = "rate_limited"


class _TokenBucket:
    """Refills continuously at `rate` tokens per second, up to `capacity`."""

    def __init__(self, *, rate: float, capacity: float) -> None:
        self._rate = rate
        self._capacity = capacity
        self._tokens = capacity
        self._updated = time.monotonic()
        self._lock = threading.Lock()

    def allow(self) -> bool:
        with self._lock:
            now = time.monotonic()
            elapsed = now - self._updated
            self._updated = now
            self._tokens = min(self._capacity, self._tokens + elapsed * self._rate)
            if self._tokens < 1.0:
                return False
            self._tokens -= 1.0
            return True


class RateLimitRegistry:
    """One pair of buckets (per-key + global) per named endpoint."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._per_key: dict[tuple[str, str], _TokenBucket] = {}
        self._global: dict[str, _TokenBucket] = {}

    def reset(self) -> None:
        """Drops every bucket. Tests use this so one case cannot starve the next."""
        with self._lock:
            self._per_key.clear()
            self._global.clear()

    def allow(
        self,
        *,
        endpoint: str,
        key: str,
        per_key_per_minute: int,
        global_per_minute: int,
    ) -> bool:
        per_key_rate = per_key_per_minute / 60.0
        global_rate = global_per_minute / 60.0

        with self._lock:
            per_key = self._per_key.get((endpoint, key))
            if per_key is None:
                per_key = _TokenBucket(rate=per_key_rate, capacity=float(per_key_per_minute))
                self._per_key[(endpoint, key)] = per_key

            global_bucket = self._global.get(endpoint)
            if global_bucket is None:
                global_bucket = _TokenBucket(rate=global_rate, capacity=float(global_per_minute))
                self._global[endpoint] = global_bucket

        # Check per-key first so one noisy key cannot empty the global bucket alone
        # without being told it was that key's problem. Both must allow.
        if not per_key.allow():
            return False
        if not global_bucket.allow():
            return False
        return True


# Process-wide. A TestClient shares the same module, so tests call reset() between cases.
registry = RateLimitRegistry()


def _limit_dependency(
    endpoint: str,
    per_key_attr: str,
    global_attr: str,
) -> Callable[..., Awaitable[None]]:
    async def dependency(
        request: Request,
        settings: Annotated[Settings, Depends(get_settings)],
        x_api_key: Annotated[str | None, Header(alias=HEADER)] = None,
    ) -> None:
        per_key = int(getattr(settings, per_key_attr))
        global_limit = int(getattr(settings, global_attr))
        # Missing key still counts against the anonymous bucket; auth will refuse it
        # separately. Grouping blanks together stops an unauthenticated flood from
        # creating a new bucket per empty header.
        key = x_api_key if x_api_key else "-"

        if not registry.allow(
            endpoint=endpoint,
            key=key,
            per_key_per_minute=per_key,
            global_per_minute=global_limit,
        ):
            logger.warning("Rate limit exceeded on %s", request.url.path)
            raise RateLimited

    return dependency


require_signals_rate_limit = _limit_dependency(
    "signals",
    "rate_limit_signals_per_minute",
    "rate_limit_signals_global_per_minute",
)

require_screen_rate_limit = _limit_dependency(
    "screen",
    "rate_limit_screen_per_minute",
    "rate_limit_screen_global_per_minute",
)
