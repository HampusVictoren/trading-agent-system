"""pgvector-backed semantic memory for the agents.

The clients and the connection pool are built once by the FastAPI lifespan and handed in,
rather than created at import time. Creating them at import bound them to whichever event
loop happened to import the module first, which is why the smoke test in CLAUDE.md had to
run inside a single asyncio.run().
"""

import asyncpg
from openai import AsyncOpenAI

# nomic-embed-text produces 768 dimensions, which is what the embedding column is declared
# as. Changing the model means changing the column, so this is deliberately not a setting.
EMBEDDING_MODEL = "nomic-embed-text"
EMBEDDING_DIMENSIONS = 768


class MemoryStore:
    """Reads and writes agent.agent_memories. One instance per process, built by the lifespan."""

    def __init__(self, pool: asyncpg.Pool, embeddings: AsyncOpenAI) -> None:
        self._pool = pool
        self._embeddings = embeddings

    async def embed(self, text: str) -> list[float]:
        response = await self._embeddings.embeddings.create(input=text, model=EMBEDDING_MODEL)
        return response.data[0].embedding

    async def save(self, ticker: str, action: str, reasoning: str) -> None:
        embedding = await self.embed(f"{ticker} {action}: {reasoning}")

        async with self._pool.acquire() as conn:
            await conn.execute(
                """
                INSERT INTO agent.agent_memories (ticker, action, reasoning, embedding)
                VALUES ($1, $2, $3, $4)
                """,
                ticker.upper(),
                action.upper(),
                reasoning,
                embedding,
            )

    async def search(self, ticker: str, query: str, limit: int = 3) -> str:
        """Returns the closest past analyses as text for an agent to read.

        The result is written for the model, which is why it is in Swedish and why a
        failure becomes a sentence rather than an exception: a missing memory must not
        end an analysis. Stage 1's error handling covers the routes, not this string.
        """
        try:
            query_embedding = await self.embed(query)

            async with self._pool.acquire() as conn:
                rows = await conn.fetch(
                    """
                    SELECT action, reasoning, created_at,
                           1 - (embedding <=> $1) AS similarity
                    FROM agent.agent_memories
                    WHERE ticker = $2
                    ORDER BY embedding <=> $1
                    LIMIT $3
                    """,
                    query_embedding,
                    ticker.upper(),
                    limit,
                )
        except Exception as e:
            return f"Kunde inte hämta historiskt minne: {e}"

        if not rows:
            return f"Inga tidigare sparade analyser hittades för {ticker}."

        return "\n".join(
            f"[{r['created_at'].strftime('%Y-%m-%d')}] Beslut: {r['action']} | "
            f"Likhet: {r['similarity']:.2f} | Motivering: {r['reasoning']}"
            for r in rows
        )
