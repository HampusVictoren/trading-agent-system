"""Give the journal an embedding, and retire agent_memories.

`agent_memories` held a ticker, a stance, a piece of reasoning and a vector. Three of those
four are now in `analysis_runs` and `step_outputs`, written by the pipeline for every
analysis that produced a signal - and written there with the **correlation id**, which is
the one thing agent_memories never had and the one thing memory needs. Without it a past
analysis cannot be joined to what happened afterwards, and a memory that can only recall
what was argued teaches a model to agree with itself.

So the table is not extended, it is replaced by the part of it that was missing: an
embedding, keyed on the run it belongs to.

It is a table of its own rather than a column on `analysis_runs`, because an embedding is
**derived data, not evidence**. `analysis_runs` is append-only on purpose; embeddings have
to be rebuildable, since changing the embedding model means recomputing every one of them.
That is also why the model's name is stored beside each vector - a mixture of two models in
one index is a similarity score that means nothing, and it has to be possible to see.

Revision ID: 202609252130
Revises: 202609252000
"""

from collections.abc import Sequence

from alembic import op

revision: str = "202609252130"
down_revision: str | None = "202609252000"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.execute("""
        CREATE TABLE agent.analysis_embeddings (
            analysis_run_id bigint      PRIMARY KEY
                                        REFERENCES agent.analysis_runs (id),
            model           varchar(64) NOT NULL,
            embedding       vector(768) NOT NULL,
            created_at      timestamptz NOT NULL DEFAULT now()
        )
    """)

    # Cosine, because that is the operator the recall query orders by. An index built for a
    # different operator class is not an error - Postgres ignores it and scans instead - so
    # the wrong one costs a sequential scan and says nothing.
    op.execute("""
        CREATE INDEX analysis_embeddings_vector_idx
            ON agent.analysis_embeddings USING hnsw (embedding vector_cosine_ops)
    """)

    # Deliberately no append-only trigger. Everything this table holds can be recomputed
    # from the journal, and being able to rewrite it is the point.

    op.execute("DROP TABLE agent.agent_memories")


def downgrade() -> None:
    # Exactly as the first migration built it, so going down and up again lands where it
    # started rather than somewhere similar.
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
    op.execute("""
        CREATE INDEX agent_memories_embedding_idx
            ON agent.agent_memories USING hnsw (embedding vector_cosine_ops)
    """)

    op.execute("DROP TABLE agent.analysis_embeddings")
