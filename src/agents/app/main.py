import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager
from typing import Annotated

import asyncpg
import httpx2
from fastapi import Depends, FastAPI
from fastapi.responses import JSONResponse
from openai import AsyncOpenAI
from pgvector.asyncpg import register_vector

from app.api.errors import register_error_handlers
from app.api.routes import router
from app.application.pipeline import SignalPipeline, TeamRuntime
from app.application.teams import TEAMS, all_roles, load_prompts
from app.application.versioning import compute_team_version
from app.dependencies import Resources, get_resources
from app.infrastructure.ag2.runner import Ag2StepRunner, build_agents
from app.infrastructure.db.memory import MemoryStore
from app.infrastructure.llm.provider import ModelConfigs, build_model_configs
from app.infrastructure.market_data.caching import CachingMarketData
from app.infrastructure.market_data.yfinance_source import fetch_from_yfinance
from app.observability.correlation import CorrelationIdMiddleware
from app.observability.logging import configure_logging
from app.settings import Settings, get_settings

logger = logging.getLogger(__name__)

# asyncpg waits 60 s by default. In WSL's mirrored networking a port with no listener
# hangs rather than refusing, so a database that is simply not running would hold startup
# for a full minute and then raise a TimeoutError with no message.
DATABASE_CONNECT_TIMEOUT_SECONDS = 10

# A readiness probe answers a load balancer, so it may not wait as long as a real call.
READINESS_TIMEOUT_SECONDS = 5


@asynccontextmanager
async def _database_pool(settings: Settings) -> AsyncIterator[asyncpg.Pool]:
    """Opens the connection pool, or fails with a message that says what to do.

    register_vector runs once per connection; pgvector needs it before a vector parameter
    can be sent. The DSN carries the agent_svc password, so it never reaches the error.
    """
    try:
        pool = await asyncpg.create_pool(
            dsn=settings.database_url.get_secret_value(),
            init=register_vector,
            min_size=1,
            max_size=5,
            timeout=DATABASE_CONNECT_TIMEOUT_SECONDS,
        )
    except (OSError, TimeoutError) as e:
        raise RuntimeError(
            "Could not reach the database at startup. Is `docker compose up -d` running?"
        ) from e

    try:
        yield pool
    finally:
        await pool.close()


def _probe_url(settings: Settings) -> str | None:
    """What /ready asks for a model list, or None when there is nothing generic to ask.

    A provider with its own endpoint - Anthropic, xAI - has no OpenAI-shaped /models, and
    guessing one would make readiness report an outage that is not there. Such a provider
    needs a probe of its own; until one runs here, readiness says so rather than pretending.
    """
    base_url = settings.llm.default.base_url
    return None if base_url is None else str(base_url).rstrip("/")


def _build_pipeline(
    settings: Settings, models: ModelConfigs, market: CachingMarketData
) -> SignalPipeline:
    """Every team, validated and ready, before the service reports itself up.

    Reading the prompts, hashing the versions and constructing the agents all happen here
    rather than on the first request, so a missing prompt file or a provider whose extra
    is not installed stops startup instead of failing an analysis an hour later.
    """
    teams: dict[str, TeamRuntime] = {}

    for team_id, spec in TEAMS.items():
        prompts = load_prompts(spec)
        version = compute_team_version(spec, prompts, settings.llm)
        teams[team_id] = TeamRuntime(
            spec=spec,
            version=version,
            runner=Ag2StepRunner(build_agents(spec, prompts, models)),
        )
        logger.info("Team '%s' is version %s with steps %s", team_id, version, spec.roles)

    return SignalPipeline(teams, market)


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """Builds every shared resource once, on the loop that will use it, and closes it again.

    Reading the settings here rather than at import time means a bad environment fails at
    startup with a message naming the setting, instead of at the first request.
    """
    configure_logging()
    settings = get_settings()

    # trust_env=False forces the client to ignore any system proxy and connect straight to
    # 127.0.0.1. openai 3.x types http_client as httpx2.AsyncClient, which is what this is.
    async with httpx2.AsyncClient(trust_env=False) as http_client:
        embeddings = AsyncOpenAI(
            base_url=str(settings.embeddings_base_url),
            api_key=settings.embeddings_api_key.get_secret_value(),
            http_client=http_client,
        )

        async with _database_pool(settings) as pool:
            models = build_model_configs(settings.llm, all_roles(TEAMS.values()))

            # One provider for the whole process: the quote endpoint and the pipeline share
            # its cache, so a symbol fetched for an analysis is not fetched again to value
            # the holding it created.
            market = CachingMarketData(
                fetch_from_yfinance,
                timeout_s=settings.market_data_timeout_s,
                ttl_s=settings.market_data_ttl_s,
            )

            app.state.resources = Resources(
                models=models,
                pipeline=_build_pipeline(settings, models, market),
                market=market,
                memory=MemoryStore(pool, embeddings),
                http_client=http_client,
                llm_base_url=_probe_url(settings),
            )
            logger.info(
                "Agent service ready: %s via %s",
                settings.llm.default.model,
                settings.llm.default.provider,
            )
            yield


app = FastAPI(
    title="Trading Agent Service",
    version="1.0.0",
    description="Python AI Agent Service for Financial Analysis",
    lifespan=lifespan,
)
app.add_middleware(CorrelationIdMiddleware)
register_error_handlers(app)
app.include_router(router)


@app.get("/health")
def health_check() -> dict[str, str]:
    """Liveness: the process is up. It deliberately checks nothing else, so that a
    restarter does not kill a service whose dependencies are merely slow."""
    return {"status": "alive", "service": "agents"}


@app.get("/ready")
async def readiness_check(resources: Annotated[Resources, Depends(get_resources)]) -> JSONResponse:
    """Readiness: the service can actually do its job. 503 until both dependencies answer."""
    checks: dict[str, str] = {}

    try:
        await resources.memory.ping()
        checks["database"] = "ok"
    except Exception:
        logger.warning("Readiness: the database did not answer", exc_info=True)
        checks["database"] = "unavailable"

    if resources.llm_base_url is None:
        checks["llm"] = "unchecked"
    else:
        try:
            response = await resources.http_client.get(
                f"{resources.llm_base_url}/models", timeout=READINESS_TIMEOUT_SECONDS
            )
            response.raise_for_status()
            checks["llm"] = "ok"
        except Exception:
            logger.warning("Readiness: the LLM backend did not answer", exc_info=True)
            checks["llm"] = "unavailable"

    # "unchecked" does not block readiness: it means this service has no way to ask, not
    # that the answer was bad.
    ready = all(state in ("ok", "unchecked") for state in checks.values())
    return JSONResponse(
        status_code=200 if ready else 503,
        content={"status": "ready" if ready else "not ready", "checks": checks},
    )
