"""Runs a team: the fact sheet first, then each step, then the signal.

Decision 4 in one loop. Every step gets exactly the earlier results its `reads` names and
starts with nothing else - no transcript, no accumulated history - so the cost of a step
is set by the schemas rather than by how many steps came before it.

Nothing here imports AG2. A step is run through the StepRunner port, which is also where
an openai timeout becomes an AnalysisError, so this file only ever deals in outcomes the
service already has a status code for.
"""

import json
from collections.abc import Mapping
from dataclasses import dataclass

from pydantic import BaseModel

from app.application.errors import AgentResponseInvalid, InstrumentNotSupported, UnknownTeam
from app.application.ports import MarketDataProvider, StepRunner
from app.application.teams import StepSpec, TeamSpec
from app.domain.facts import FactSheet, build_fact_sheet
from app.domain.signals import RunInfo, SignalRequest, TradeSignal, TradeView

# The prompts tell the model that everything between these is data rather than
# instructions. Kept here because the delimiter is part of that agreement.
DATA_OPEN = "<data>"
DATA_CLOSE = "</data>"

# Agent-facing text, so Swedish, like the prompt files. The model reads these labels.
INSTRUMENT_LABEL = "Instrument"
POSITION_LABEL = "Nuvarande innehav"
NO_POSITION = "inget"

# No review rounds yet. The field is in the contract from the start because adding one
# later would be a contract change; stage 4's outcomes decide whether rounds are worth it.
REVISIONS = 0


def build_message(step: StepSpec, request: SignalRequest, context: Mapping[str, BaseModel]) -> str:
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

    payload = {name: model.model_dump(mode="json") for name, model in context.items()}
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
        result: BaseModel | None = None

        for step in team.spec.steps:
            context = {schema.__name__: outputs[schema] for schema in step.reads}
            result = await team.runner.run_step(
                step.role, build_message(step, request, context), step.output_schema
            )
            outputs[step.output_schema] = result

        if not isinstance(result, TradeView):
            # TeamSpec makes this unreachable, and it stays because the alternative to an
            # explicit failure is returning something the engine cannot read.
            raise AgentResponseInvalid(
                f"Team '{team.spec.id}' ended with {type(result).__name__}, not a view."
            )

        # The agents' half, joined with the half code is responsible for. The price comes
        # from the fact sheet, never from the model: the engine sizes the order from it.
        return TradeSignal.from_view(
            result,
            instrument=request.instrument,
            reference_price=facts.price,
            quote_as_of=facts.as_of,
            run=RunInfo(team_id=team.spec.id, team_version=team.version, revisions=REVISIONS),
        )
