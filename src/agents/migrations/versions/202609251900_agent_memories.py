"""Take agent.agent_memories over from the init script.

The table has existed since the first week of the project, created by
db/init/01-schema.sh. That script runs once, on an empty volume, and never again - so
every change to the agents' schema so far has meant destroying the database and starting
over. This migration is the same table, written as a migration, and the init script no
longer creates it.

An existing database therefore has the table but no version row. Stamp it rather than
running this against it:

    uv run alembic stamp 202609251900

Revision ID: 202609251900
Revises:
"""

from collections.abc import Sequence

from alembic import op

revision: str = "202609251900"
down_revision: str | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # Written as SQL rather than through op.create_table, because `vector(768)` is a
    # pgvector type SQLAlchemy has no column for without another dependency - and because
    # this migration has to reproduce what the init script wrote, character for character,
    # rather than something equivalent that a type mapping happened to produce.
    op.execute("""
        CREATE TABLE agent.agent_memories (
            id         bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            ticker     varchar(10) NOT NULL,
            action     varchar(10) NOT NULL,
            reasoning  text        NOT NULL,
            embedding  vector(768) NOT NULL,
            created_at timestamptz NOT NULL DEFAULT now()
        )
    """)

    # HNSW with cosine distance, because MemoryStore.search orders by `<=>`. An index
    # built for a different operator class would be ignored by that query rather than
    # refused, so the wrong one costs a sequential scan and says nothing.
    op.execute("""
        CREATE INDEX agent_memories_embedding_idx
            ON agent.agent_memories USING hnsw (embedding vector_cosine_ops)
    """)


def downgrade() -> None:
    # The index belongs to the table and goes with it.
    op.execute("DROP TABLE agent.agent_memories")
