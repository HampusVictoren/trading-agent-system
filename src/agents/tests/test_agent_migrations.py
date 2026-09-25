"""The agent schema, proved against a real database under the real role.

What these check is not "does SQL work" but the two things that only show up against a
live server: that `agent_svc` is *allowed* to do what the migration asks, and that the
table the migration builds is the one the code in memory.py queries.
"""

import asyncpg
import pytest

from app.infrastructure.db.memory import EMBEDDING_DIMENSIONS
from tests.conftest import alembic_config, downgrade, upgrade


async def _fetch(dsn: str, query: str, *args: object) -> list[asyncpg.Record]:
    connection = await asyncpg.connect(dsn)
    try:
        return await connection.fetch(query, *args)
    finally:
        await connection.close()


@pytest.mark.parametrize(
    "table", ["analysis_runs", "step_outputs", "signal_outcomes", "analysis_embeddings"]
)
async def test_every_table_is_owned_by_the_role_that_owns_the_schema(
    migrated: str, table: str
) -> None:
    rows = await _fetch(
        migrated,
        """
        SELECT tableowner FROM pg_tables
        WHERE schemaname = 'agent' AND tablename = $1
        """,
        table,
    )

    # Owned by agent_svc rather than by the superuser, which is what lets the service
    # alter its own tables later without anyone granting it anything.
    assert [r["tableowner"] for r in rows] == ["agent_svc"]


async def test_alembics_bookkeeping_lives_in_the_schema_this_service_owns(
    migrated: str,
) -> None:
    """Where Alembic keeps its own row is a decision, not a detail.

    Worth knowing what this does and does not catch, because the obvious mutation does
    not bite: deleting `version_table_schema` from env.py leaves every test green, since
    agent_svc's search_path already resolves to `agent`. What fails here is pointing it
    somewhere else - `public` is permission denied, and a second schema would split the
    history in two. The engine needs the same line for a stronger reason: EF Core
    hardcodes `public` instead of following a search path, so there it was a real bug.
    """
    rows = await _fetch(
        migrated,
        "SELECT schemaname FROM pg_tables WHERE tablename = 'alembic_version'",
    )

    assert [r["schemaname"] for r in rows] == ["agent"]


async def test_the_embedding_column_is_as_wide_as_the_model_this_service_pins(
    migrated: str,
) -> None:
    """768 is nomic-embed-text's output, and the column width is the reason the model is
    pinned in code rather than configurable. This is the assertion that would fail if
    somebody made it a setting without changing the schema."""
    rows = await _fetch(
        migrated,
        """
        SELECT format_type(a.atttypid, a.atttypmod) AS type
        FROM pg_attribute a
        WHERE a.attrelid = 'agent.analysis_embeddings'::regclass AND a.attname = 'embedding'
        """,
    )

    assert [r["type"] for r in rows] == [f"vector({EMBEDDING_DIMENSIONS})"]


async def test_the_index_is_built_for_the_operator_the_search_actually_uses(
    migrated: str,
) -> None:
    """AnalysisMemory.recall orders by `<=>`, cosine distance. An index built with a
    different operator class is not an error - Postgres simply ignores it and scans the
    table, so the only symptom is that recall gets slower as the journal grows."""
    rows = await _fetch(
        migrated,
        """
        SELECT indexdef FROM pg_indexes
        WHERE schemaname = 'agent' AND indexname = 'analysis_embeddings_vector_idx'
        """,
    )

    assert len(rows) == 1
    definition = rows[0]["indexdef"]
    assert "USING hnsw" in definition
    assert "vector_cosine_ops" in definition


async def test_downgrading_leaves_the_schema_as_the_init_script_left_it(
    agent_database: str,
) -> None:
    """Every test here already downgrades on its way out; this is the one that says what
    downgrading has to mean. A rollback that leaves tables behind is a deploy that cannot
    be repeated."""
    config = alembic_config(agent_database)
    upgrade(config)
    downgrade(config)

    rows = await _fetch(
        agent_database,
        "SELECT tablename FROM pg_tables WHERE schemaname = 'agent' ORDER BY tablename",
    )

    # alembic_version survives `downgrade base` by design - it is Alembic's own row, and
    # it is empty. Nothing of this service's is left.
    assert [r["tablename"] for r in rows] == ["alembic_version"]


async def test_the_agent_role_cannot_reach_the_engines_schema(migrated: str) -> None:
    """The other half of decision 2, from this side. The engine's own suite proves
    engine_svc cannot see `agent`; this proves the reverse, so neither service can quietly
    turn the database into the integration point."""
    connection = await asyncpg.connect(migrated)
    try:
        with pytest.raises(asyncpg.InsufficientPrivilegeError):
            await connection.execute("CREATE TABLE trading.sneaky (id int)")
    finally:
        await connection.close()
