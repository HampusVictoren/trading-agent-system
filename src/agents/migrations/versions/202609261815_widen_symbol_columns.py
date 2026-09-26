"""Widen every symbol column to sixteen characters, for Swedish tickers.

The universe moved to Stockholm, and two symbols do not fit ten characters: `ESSITY-B.ST` is
11 and a real OMXS30 member, and `XACT-OMXS30.ST` is 14 and is what outcomes are measured
against. The second one was already broken rather than merely tight - `POST /v1/outcomes`
would have refused every row the engine posted after the benchmark changed, with
`value too long for type character varying(10)`, and it would have refused them nightly.

Sixteen matches `MAX_SYMBOL_LENGTH` in `app.domain.signals` and the engine's
`TickerColumn.MaxLength`. Three copies of one number, which is one more than anybody wants;
the contract tests hold the first two together and the round-trip test in
`tests/test_outcome_store.py` is what holds this one.

Revision ID: 202609261815
Revises: 202609252130
"""

from collections.abc import Sequence

from alembic import op

revision: str = "202609261815"
down_revision: str | None = "202609252130"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    # Widening a varchar is a catalogue change rather than a table rewrite, so this is cheap
    # whatever the table holds. Nothing has to be re-validated: every existing value already
    # fits the wider type.
    op.execute("ALTER TABLE agent.analysis_runs ALTER COLUMN symbol TYPE varchar(16)")
    op.execute("ALTER TABLE agent.signal_outcomes ALTER COLUMN benchmark_symbol TYPE varchar(16)")


def downgrade() -> None:
    # Narrowing will refuse if any stored symbol has grown past ten by then, which is inherent
    # to reversing this rather than a flaw in it: the data would no longer fit the schema it is
    # being returned to.
    op.execute("ALTER TABLE agent.analysis_runs ALTER COLUMN symbol TYPE varchar(10)")
    op.execute("ALTER TABLE agent.signal_outcomes ALTER COLUMN benchmark_symbol TYPE varchar(10)")
