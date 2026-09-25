"""Runs a team: the fact sheet first, then each step, then the signal.

Decision 4 in one loop. Every step gets exactly the earlier results its `reads` names and
starts with nothing else - no transcript, no accumulated history - so the cost of a step
is set by the schemas rather than by how many steps came before it.

Nothing here imports AG2. A step is run through the StepRunner port, which is also where
an openai timeout becomes an AnalysisError, so this file only ever deals in outcomes the
service already has a status code for.
"""

import json
import logging
from collections.abc import Mapping
from dataclasses import dataclass

from pydantic import BaseModel

from app.application.errors import AgentResponseInvalid, InstrumentNotSupported, UnknownTeam
from app.application.journal import AnalysisRun, RecordedStep
from app.application.ports import AnalysisJournal, MarketDataProvider, Memory, StepRunner
from app.application.teams import StepSpec, TeamSpec
from app.domain.facts import FactSheet, build_fact_sheet
from app.domain.signals import RunInfo, SignalRequest, TradeSignal, TradeView
from app.domain.steps import MarketRead

logger = logging.getLogger(__name__)

# The prompts tell the model that everything between these is data rather than
# instructions. Kept here because the delimiter is part of that agreement.
DATA_OPEN = "<data>"
DATA_CLOSE = "</data>"

# Agent-facing text, so Swedish, like the prompt files. The model reads these labels.
INSTRUMENT_LABEL = "Instrument"
POSITION_LABEL = "Nuvarande innehav"
NO_POSITION = "inget"

# Memory travels inside the data block like everything else, under a key of its own. The
# other keys are schema names and happen to be English because they are type names; this
# one is prose written for a model, so it follows the rule the prompts follow.
MEMORY_KEY = "Minne"

# No review rounds yet. The field is in the contract from the start because adding one
# later would be a contract change; stage 4's outcomes decide whether rounds are worth it.
REVISIONS = 0


def describe_reading(read: MarketRead) -> str:
    """The text a run is embedded by, and recalled with.

    One function for both ends on purpose. Memory is only meaningful if the vector written
    after a run and the query asked before the next one are the same kind of text - and a
    symmetry kept by two call sites is a symmetry that lasts until somebody edits one.
    """
    return f"{read.trend} {read.valuation}: {' '.join(read.observations)}"


def build_message(
    step: StepSpec,
    request: SignalRequest,
    context: Mapping[str, BaseModel],
    memory: str | None = None,
) -> str:
    """Everything the step is told, and nothing else.

    The prompt file holds the instructions; this holds the run. Keeping them apart is what
    makes the prompt safe to hash and makes this exactly assertable in a test.
    """
    lines = [f"{INSTRUMENT_LABEL}: {request.instrument.symbol}"]

    if step.sees_position:
        position = request.existing_position
        lines.append(
            f"{POSITION_LABEL}: {NO_POSITION}"
            if position is None
            else f"{POSITION_LABEL}: {position.quantity} st till snittkurs {position.average_price}"
        )

    payload: dict[str, object] = {
        name: model.model_dump(mode="json") for name, model in context.items()
    }

    # Inside the delimiters, not before them. It is our own model's past prose, which is
    # still text a model wrote - the one place it could carry an instruction is the one
    # place the prompts say instructions are not obeyed.
    if memory is not None:
        payload[MEMORY_KEY] = memory
    # sort_keys so the same context always produces the same message, which a test can
    # compare against; ensure_ascii=False so Swedish text stays readable and cheap.
    lines += [DATA_OPEN, json.dumps(payload, sort_keys=True, ensure_ascii=False), DATA_CLOSE]

    return "\n".join(lines)


@dataclass(frozen=True)
class TeamRuntime:
    """A team, ready to run: its specification, its version, and something to run it with."""

    spec: TeamSpec
    version: str
    runner: StepRunner


@dataclass(frozen=True)
class SignalPipeline:
    teams: Mapping[str, TeamRuntime]
    market: MarketDataProvider

    # Written to after the answer is built, and never able to withhold one. See _journal.
    journal: AnalysisJournal

    # Read before a step that asks for it, written after the run. Best-effort at both ends.
    memory: Memory

    async def run(self, request: SignalRequest) -> TradeSignal:
        team = self.teams.get(request.team_id)
        if team is None:
            # A 422: the engine asked for a setup that does not exist. Falling back to a
            # default team would answer a question nobody asked.
            raise UnknownTeam(
                f"No team '{request.team_id}'. The teams that exist are {sorted(self.teams)}."
            )

        if request.instrument.type not in team.spec.instrument_types:
            raise InstrumentNotSupported(
                f"Team '{team.spec.id}' does not cover {request.instrument.type} instruments."
            )

        facts = build_fact_sheet(await self.market.snapshot(request.instrument.symbol))

        # Indexed by schema type rather than by step, because that is what `reads` names.
        # TeamSpec has already refused two steps producing the same type, so nothing here
        # can be overwritten.
        outputs: dict[type[BaseModel], BaseModel] = {FactSheet: facts}
        recorded: list[RecordedStep] = []
        result: BaseModel | None = None

        for ordinal, step in enumerate(team.spec.steps):
            context = {schema.__name__: outputs[schema] for schema in step.reads}
            recalled = await self._recall(step, request, outputs)
            result = await team.runner.run_step(
                step.role,
                build_message(step, request, context, recalled),
                step.output_schema,
            )
            outputs[step.output_schema] = result
            recorded.append(RecordedStep(ordinal=ordinal, role=step.role, output=result))

        if not isinstance(result, TradeView):
            # TeamSpec makes this unreachable, and it stays because the alternative to an
            # explicit failure is returning something the engine cannot read.
            raise AgentResponseInvalid(
                f"Team '{team.spec.id}' ended with {type(result).__name__}, not a view."
            )

        # The agents' half, joined with the half code is responsible for. The price comes
        # from the fact sheet, never from the model: the engine sizes the order from it.
        signal = TradeSignal.from_view(
            result,
            instrument=request.instrument,
            reference_price=facts.price,
            quote_as_of=facts.as_of,
            run=RunInfo(team_id=team.spec.id, team_version=team.version, revisions=REVISIONS),
        )

        run_id = await self._journal(request, team, facts, tuple(recorded))
        if run_id is not None:
            await self._remember(request, run_id, outputs)

        return signal

    async def _recall(
        self,
        step: StepSpec,
        request: SignalRequest,
        outputs: Mapping[type[BaseModel], BaseModel],
    ) -> str | None:
        """What this step is told about the last time the market looked like this.

        None for a step that does not ask, so the message is byte-for-byte what it was
        before memory existed - which is what keeps `default` comparable to itself across
        this change, and its team_version honest.

        TeamSpec has already refused a step that asks for memory without reading
        MarketRead, so the lookup below cannot miss.
        """
        if not step.sees_memory:
            return None

        read = outputs[MarketRead]
        assert isinstance(read, MarketRead)  # noqa: S101 - TeamSpec guarantees it

        return await self.memory.recall(
            request.instrument.symbol,
            describe_reading(read),
            correlation_id=request.correlation_id,
        )

    async def _journal(
        self,
        request: SignalRequest,
        team: TeamRuntime,
        facts: FactSheet,
        steps: tuple[RecordedStep, ...],
    ) -> int | None:
        """Stores the run and returns its id, or None when it could not be stored.

        Only successful runs reach here, which makes the population "analyses that produced
        a signal". The engine's own `decisions` is wider - it has a row for a cycle this
        service failed - so a correlation id present there and missing here is either a
        failure the engine already recorded or a journal write that did not land. The log
        line below is what tells the two apart.

        The write happens after the signal is built rather than before the steps, so a
        half-finished run does not look like a team that answered with nothing. The cost is
        that a failure mid-team leaves no trace here; the engine records that it happened,
        and the reason is in this service's log under the same id.
        """
        try:
            return await self.journal.record(
                AnalysisRun(
                    correlation_id=request.correlation_id,
                    team_id=team.spec.id,
                    team_version=team.version,
                    instrument_type=request.instrument.type,
                    symbol=request.instrument.symbol,
                    facts=facts,
                    steps=steps,
                )
            )
        except Exception:
            # Deliberately not raised. An answer the engine can act on is worth more than
            # a complete journal, and the engine cannot tell a storage failure here from
            # the agents failing - it would read as "no decision this cycle".
            logger.error(
                "Analysis %s was not journalled, so its working is lost",
                request.correlation_id,
                exc_info=True,
            )
            return None

    async def _remember(
        self,
        request: SignalRequest,
        run_id: int,
        outputs: Mapping[type[BaseModel], BaseModel],
    ) -> None:
        """Embeds the run, so the next analysis of this instrument can recall it.

        Every team contributes, including one that never reads memory - so a team switched
        on later has something to recall from its first cycle rather than from its first
        measured horizon a week afterwards. A team with no MarketRead contributes nothing;
        there is no such team, and building for one would be generalising from a single case.

        Its own try, and its own severity, because it fails for its own reasons - it calls
        an embedding model over the network - and because sharing the journal's made the
        journal's log line lie. That line is how a hole in the journal is found at all: a
        correlation id the engine has in `decisions` with no run on this side. An embedding
        that failed is not such a hole, and reporting it as one sends a reader looking for
        a missing row that is sitting right there.

        A warning rather than an error, because nothing is lost that cannot be rebuilt.
        That is the whole reason the embedding lives in a table of its own.
        """
        read = outputs.get(MarketRead)
        if not isinstance(read, MarketRead):
            return

        try:
            await self.memory.remember(run_id, describe_reading(read))
        except Exception:
            logger.warning(
                "Analysis %s was journalled but not embedded, so it cannot be recalled "
                "until something rebuilds it",
                request.correlation_id,
                exc_info=True,
            )
