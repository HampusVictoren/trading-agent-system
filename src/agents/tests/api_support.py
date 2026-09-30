"""Shared stubs for HTTP tests of the agent API.

Route tests override get_settings with a SimpleNamespace rather than a full Settings,
so every new security control that reads a setting has to appear here too - otherwise a
test that only cared about the pipeline starts failing with AttributeError.
"""

from types import SimpleNamespace

from pydantic import SecretStr

API_KEY = "a-test-key-of-some-length"
OUTCOMES_HMAC_SECRET = "a-test-outcomes-hmac-secret"


def api_settings(**overrides: object) -> SimpleNamespace:
    """Settings a TestClient needs for auth, rate limits and outcomes integrity."""
    values = dict(
        agent_api_key=SecretStr(API_KEY),
        agent_api_key_signals=None,
        agent_api_key_screen=None,
        agent_api_key_outcomes=None,
        agent_api_key_market=None,
        outcomes_hmac_secret=SecretStr(OUTCOMES_HMAC_SECRET),
        # High enough that ordinary route tests never trip the limiter.
        rate_limit_signals_per_minute=1_000,
        rate_limit_signals_global_per_minute=1_000,
        rate_limit_screen_per_minute=1_000,
        rate_limit_screen_global_per_minute=1_000,
        enable_docs=False,
        ready_detail=False,
        environment="development",
        bind_host="127.0.0.1",
    )
    values.update(overrides)
    return SimpleNamespace(**values)
