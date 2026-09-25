"""Record what the agents were given and what each of them answered.

The engine stores what the engine saw - the request, the signal, the outcome. This is the
other half, on the side that owns it: the fact sheet the analysis started from, and every
step's answer, against the same correlation id. Nothing joins them in the database; the
join is made when somebody asks a question, which is what keeps two services out of one
schema.

Two questions need this table, and neither can be answered afterwards without it.

*Replay*, which stage 8 wants: run a different team over the exact inputs an old decision
had, rather than over today's prices. Only the stored fact sheet makes that the same
question twice.

*Attribution*, which stage 8 needs before more agents are worth adding: comparing two
team_versions says team B did better, not that the news agent was the reason. The step
outputs are what turns "which team" into "which step changed its mind".

Revision ID: 202609251930
Revises: 202609251900
"""

from collections.abc import Sequence

from alembic import op

revision: str = "202609251930"
down_revision: str | None = "202609251900"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # varchar(64) on the three identifiers, because that is exactly what the engine's
    # `decisions` table uses for them. A join key that is wider on one side is a join key
    # that fails on a value only one side would accept.
    op.execute("""
        CREATE TABLE agent.analysis_runs (
            id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            correlation_id  varchar(64) NOT NULL UNIQUE,
            team_id         varchar(64) NOT NULL,
            team_version    varchar(64) NOT NULL,
            instrument_type varchar(16) NOT NULL,
            symbol          varchar(10) NOT NULL,
            fact_sheet      jsonb       NOT NULL,
            created_at      timestamptz NOT NULL DEFAULT now()
        )
    """)

    # The unique correlation_id above is the guard that matters: one analysis is one row,
    # the same rule decisions.correlation_id gives a cycle. This index is for the question
    # the table will actually be read with - what did we think about MSFT lately.
    op.execute("""
        CREATE INDEX analysis_runs_symbol_created_idx
            ON agent.analysis_runs (symbol, created_at DESC)
    """)

    # jsonb rather than columns per field. A step's schema is the team's business and
    # changes with team_version - modelling it here would mean a migration every time a
    # prompt's output grew a field, and a table that cannot hold two teams at once.
    op.execute("""
        CREATE TABLE agent.step_outputs (
            id          bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            run_id      bigint      NOT NULL REFERENCES agent.analysis_runs (id),
            ordinal     smallint    NOT NULL,
            role        varchar(64) NOT NULL,
            schema_name varchar(64) NOT NULL,
            output      jsonb       NOT NULL,
            UNIQUE (run_id, role)
        )
    """)

    # Append-only, like `orders` and `decisions` on the engine's side, and for the same
    # reason: evidence that can be edited afterwards is not evidence. A replay is only
    # worth running if the inputs it replays are the inputs that were used.
    #
    # It stops application code, not the schema's owner: agent_svc owns these tables and
    # can drop the trigger. A migration may change the rules; an analysis may not.
    op.execute("""
        CREATE FUNCTION agent.refuse_rewriting_history() RETURNS trigger
            LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'agent.% is append-only; % is not allowed',
                TG_TABLE_NAME, TG_OP;
        END;
        $$;
    """)

    # FOR EACH STATEMENT, so a DELETE that happens to match no rows is refused too. A rule
    # that only fires when it finds something is a rule that passes its own test by
    # accident.
    for table in ("analysis_runs", "step_outputs"):
        op.execute(f"""
            CREATE TRIGGER {table}_are_append_only
                BEFORE UPDATE OR DELETE ON agent.{table}
                FOR EACH STATEMENT EXECUTE FUNCTION agent.refuse_rewriting_history();
        """)  # noqa: S608 - a literal from the tuple above, not a value from anywhere


def downgrade() -> None:
    # Dropping a table takes its triggers with it, but the function belongs to the schema.
    # Leaving it behind would make the next upgrade fail on "function already exists",
    # which is the failure a downgrade exists to prevent.
    op.execute("DROP TABLE agent.step_outputs")
    op.execute("DROP TABLE agent.analysis_runs")
    op.execute("DROP FUNCTION agent.refuse_rewriting_history()")
