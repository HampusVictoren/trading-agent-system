"""Keep a copy of what the engine measured.

The engine owns the measurement. trading.signal_outcomes is the record, its
OutcomeCalculator is the definition of a hit, and neither is this service's business. What
this table is for is memory: an analysis that can only recall what was argued teaches a
model to agree with itself, and only what happened afterwards can make the next decision
better.

Two deliberate absences.

*No foreign key to analysis_runs.* The engine measures every decision it made, including
cycles this service never produced a signal for and decisions from before the journal
existed. A constraint would turn "we have no record of that analysis" into a rejected
batch.

*No unique violation on a repeat.* The write is ON CONFLICT DO NOTHING, so a sweep that is
retried does not have to know what landed the first time. This table is a copy, not the
record - a duplicate here is genuinely nothing new, which is not true of analysis_runs.

Revision ID: 202609252000
Revises: 202609251930
"""

from collections.abc import Sequence

from alembic import op

revision: str = "202609252000"
down_revision: str | None = "202609251930"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # numeric, not double precision, and at the same precision as the engine's own
    # columns: a copy that rounds differently from the original is a copy that disagrees
    # with it the first time anyone checks.
    op.execute("""
        CREATE TABLE agent.signal_outcomes (
            id                bigint       GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            correlation_id    varchar(64)  NOT NULL,
            horizon_unit      varchar(16)  NOT NULL,
            horizon_days      integer      NOT NULL,
            status            varchar(16)  NOT NULL,
            reason            text,
            benchmark_symbol  varchar(10)  NOT NULL,
            measured_on       date,
            measured_price    numeric(18,8),
            instrument_return numeric(12,6),
            benchmark_return  numeric(12,6),
            excess_return     numeric(12,6),
            cost_fraction     numeric(12,6),
            net_edge          numeric(12,6),
            hit               boolean,
            received_at       timestamptz  NOT NULL DEFAULT now(),
            UNIQUE (correlation_id, horizon_unit, horizon_days)
        )
    """)

    # What memory reads: everything known about one past analysis, by its id.
    op.execute("""
        CREATE INDEX signal_outcomes_correlation_idx
            ON agent.signal_outcomes (correlation_id)
    """)

    # Append-only like the journal beside it. The engine's row is the authority, so a
    # correction arrives as a new measurement there and a new row here - never as an edit
    # to a number somebody has already read.
    op.execute("""
        CREATE TRIGGER signal_outcomes_are_append_only
            BEFORE UPDATE OR DELETE ON agent.signal_outcomes
            FOR EACH STATEMENT EXECUTE FUNCTION agent.refuse_rewriting_history();
    """)


def downgrade() -> None:
    op.execute("DROP TABLE agent.signal_outcomes")
