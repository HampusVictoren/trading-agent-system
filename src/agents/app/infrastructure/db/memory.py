import os
import asyncpg
import httpx
from openai import AsyncOpenAI
from pgvector.asyncpg import register_vector

DB_URL = os.getenv("DATABASE_URL", "postgresql://postgres:postgres@localhost:5432/tradingdb")
OLLAMA_URL = os.getenv("OLLAMA_BASE_URL", "http://127.0.0.1:11434/v1")

# trust_env=False tvingar httpx att ignorera proxy och ansluta direkt till 127.0.0.1
http_client = httpx.AsyncClient(trust_env=False)

client = AsyncOpenAI(
    base_url=OLLAMA_URL,
    api_key="ollama",
    http_client=http_client
)

async def get_embedding(text: str) -> list[float]:
    response = await client.embeddings.create(
        input=text,
        model="nomic-embed-text"
    )
    return response.data[0].embedding

async def save_memory(ticker: str, action: str, reasoning: str):
    conn = await asyncpg.connect(DB_URL)
    await register_vector(conn)
    
    embedding = await get_embedding(f"{ticker} {action}: {reasoning}")
    await conn.execute(
        """
        INSERT INTO agent_memories (ticker, action, reasoning, embedding)
        VALUES ($1, $2, $3, $4)
        """,
        ticker.upper(), action.upper(), reasoning, embedding
    )
    await conn.close()

async def search_past_memories(ticker: str, query: str, limit: int = 3) -> str:
    try:
        conn = await asyncpg.connect(DB_URL)
        await register_vector(conn)
        
        query_embedding = await get_embedding(query)
        rows = await conn.fetch(
            """
            SELECT action, reasoning, created_at,
                   1 - (embedding <=> $1) AS similarity
            FROM agent_memories
            WHERE ticker = $2
            ORDER BY embedding <=> $1
            LIMIT $3
            """,
            query_embedding, ticker.upper(), limit
        )
        await conn.close()

        if not rows:
            return f"Inga tidigare sparade analyser hittades för {ticker}."

        results = []
        for r in rows:
            results.append(
                f"[{r['created_at'].strftime('%Y-%m-%d')}] Beslut: {r['action']} | "
                f"Likhet: {r['similarity']:.2f} | Motivering: {r['reasoning']}"
            )
        return "\n".join(results)
    except Exception as e:
        return f"Kunde inte hämta historiskt minne: {str(e)}"
