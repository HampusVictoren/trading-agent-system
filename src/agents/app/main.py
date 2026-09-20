import logging
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import asyncpg
import httpx2
from fastapi import FastAPI
from openai import AsyncOpenAI
from pgvector.asyncpg import register_vector

from app.api.routes import router
from app.dependencies import Resources
from app.infrastructure.ag2.config import build_llm_config
from app.infrastructure.db.memory import MemoryStore
from app.settings import Settings, get_settings

logger = logging.getLogger(__name__)

# asyncpg waits 60 s by default. In WSL's mirrored networking a port with no listener
# hangs rather than refusing, so a database that is simply not running would hold startup
# for a full minute and then raise a TimeoutError with no message.
DATABASE_CONNECT_TIMEOUT_SECONDS = 10


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


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """Builds every shared resource once, on the loop that will use it, and closes it again.

    Reading the settings here rather than at import time means a bad environment fails at
    startup with a message naming the setting, instead of at the first request.
    """
    settings = get_settings()

    # trust_env=False forces the client to ignore any system proxy and connect straight to
    # 127.0.0.1. openai 3.x types http_client as httpx2.AsyncClient, which is what this is.
    async with httpx2.AsyncClient(trust_env=False) as http_client:
        embeddings = AsyncOpenAI(
            base_url=str(settings.ollama_base_url),
            api_key="ollama",
            http_client=http_client,
        )

        async with _database_pool(settings) as pool:
            app.state.resources = Resources(
                llm_config=build_llm_config(settings),
                memory=MemoryStore(pool, embeddings),
            )
            logger.info("Agent service ready: model %s", settings.llm_model)
            yield


app = FastAPI(
    title="Trading Agent Service",
    version="1.0.0",
    description="Python AI Agent Service for Financial Analysis",
    lifespan=lifespan,
)
app.include_router(router)


@app.get("/health")
def health_check():
    return {"status": "online", "service": "agents"}
