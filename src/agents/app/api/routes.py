from typing import Annotated

from fastapi import APIRouter, Depends, Path

from app.api.security import require_api_key
from app.dependencies import Resources, get_resources
from app.domain.models import InvestmentProposal
from app.domain.signals import SignalRequest, TradeSignal
from app.infrastructure.ag2.team import run_agent_analysis

# The dependency sits on the router rather than on the route, so a route added later is
# closed by default. /health and /ready are defined outside it and stay open.
router = APIRouter(dependencies=[Depends(require_api_key)])

# A ticker reaches yfinance, and in the engine it is interpolated into a URL path.
# Anything outside this is a bad request, not something to spend an LLM run on.
TICKER_PATTERN = r"^[A-Za-z][A-Za-z0-9.-]{0,9}$"


@router.post("/analyze/{ticker}", response_model=InvestmentProposal)
async def analyze_ticker(
    ticker: Annotated[str, Path(pattern=TICKER_PATTERN)],
    resources: Annotated[Resources, Depends(get_resources)],
) -> InvestmentProposal:
    return await run_agent_analysis(ticker, resources.models)


@router.post("/v1/signals", response_model=TradeSignal)
async def create_signal(
    request: SignalRequest,
    resources: Annotated[Resources, Depends(get_resources)],
) -> TradeSignal:
    """The new contract. The instrument travels in the body as a typed object.

    Versioned in the path from the first day it exists: the engine and this service are
    deployed separately, so a breaking change has to be able to run beside the old shape
    rather than replace it under a running caller.

    A malformed request never reaches the pipeline - FastAPI validates it against
    SignalRequest first, which is where the symbol's format rule lives - and an unknown
    team_id or an instrument this team does not cover is refused before a model is paid for.
    """
    return await resources.pipeline.run(request)
